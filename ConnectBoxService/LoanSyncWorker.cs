using ConnectBoxService.Models;
using ConnectBoxService.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ConnectBoxService
{
    public class LoanSyncWorker : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<LoanSyncWorker> _logger;

        private readonly Dictionary<int, DateTime> _lastRunTimes = new();
        private List<ContractLmsConnection> _connections = new();

        private string? _cachedToken;
        private DateTime _tokenExpiry = DateTime.MinValue;

        public LoanSyncWorker(IServiceProvider serviceProvider, ILogger<LoanSyncWorker> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LoanSyncWorker started at {Time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Reload every cycle so contracts added after startup are picked up.
                    await LoadConnectionsAsync();

                    if (_connections.Count == 0)
                    {
                        _logger.LogWarning("No ContractLmsConnections found. Sleeping.");
                    }
                    else
                    {
                        // catId → entityId for every category that received records this cycle.
                        var affectedCategories = new Dictionary<int, int>();

                        foreach (var connection in _connections)
                        {
                            if (connection.NextPaymentsFetch.HasValue
                                    ? connection.NextPaymentsFetch <= DateTime.Now
                                    : true)
                            {
                                _logger.LogInformation(
                                    "Payments sync — ContractId {ContractId} ({Name}, every {Min} mins).",
                                    connection.ContractId,
                                    connection.PaymentsRefreshCycleName,
                                    connection.PaymentsRefreshCycleMinutes);

                                await SyncPaymentsAsync(connection, stoppingToken);
                            }

                            if (connection.NextDataFetch.HasValue
                                    ? connection.NextDataFetch <= DateTime.Now
                                    : true)
                            {
                                _logger.LogInformation(
                                    "Data sync — ContractId {ContractId} ({Name}, every {Min} mins).",
                                    connection.ContractId,
                                    connection.DataRefreshCycleName,
                                    connection.DataRefreshCycleMinutes);

                                // UpsertLoansAsync now handles cross-contract placement and logs
                                // migrations internally. Returns category IDs that received records.
                                var syncCategories = await SyncContractAsync(connection, stoppingToken);
                                _lastRunTimes[connection.ContractId] = DateTime.UtcNow;

                                if (int.TryParse(connection.EntityId, out int entId))
                                {
                                    foreach (var catId in syncCategories)
                                        affectedCategories.TryAdd(catId, entId);
                                }

                                // Between-sync migration: handles records whose stored DaysinArrears /
                                // amounts have drifted since the last LMS fetch. If anything moved,
                                // add all connection categories so they get re-allocated.
                                _logger.LogInformation("Running between-sync migration pass...");
                                int migrated = await MigrateContractRecordsAsync(stoppingToken);

                                if (migrated > 0)
                                {
                                    foreach (var c in _connections)
                                    {
                                        if (int.TryParse(c.CategoryId, out int catId) && int.TryParse(c.EntityId, out int entyId))
                                            affectedCategories.TryAdd(catId, entyId);
                                    }
                                }

                                // Allocate agents once across all categories that were touched this cycle.
                                if (affectedCategories.Count > 0)
                                {
                                    _logger.LogInformation(
                                        "Allocating agents for {Count} affected category(ies).",
                                        affectedCategories.Count);

                                    await AllocateAffectedCategoriesAsync(affectedCategories, stoppingToken);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error in worker loop.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }

            _logger.LogInformation("LoanSyncWorker stopped.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Data sync — returns the set of category IDs that received records.
        // Allocation is intentionally NOT called here; it runs once at the end
        // of the cycle across all affected categories (including migrations).
        // ─────────────────────────────────────────────────────────────────────
        private async Task<HashSet<int>> SyncContractAsync(ContractLmsConnection connection, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var loanApiService  = scope.ServiceProvider.GetRequiredService<ILoanApiService>();
                var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();

                if (_cachedToken == null || DateTime.UtcNow >= _tokenExpiry)
                {
                    _logger.LogInformation("Requesting new API token for {Entity}...", connection.LmsEntityId);
                    _cachedToken = await loanApiService.GetTokenAsync(connection.LmsEntityId);

                    if (_cachedToken == null)
                    {
                        _logger.LogError("Failed to obtain API token. Skipping ContractId {ContractId}.", connection.ContractId);
                        return new HashSet<int>();
                    }

                    _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
                }

                var loans = await loanApiService.GetLoansAsync(_cachedToken, connection);

                _logger.LogInformation(
                    "Fetched {Count} loans for ContractId {ContractId}.",
                    loans.Count, connection.ContractId);

                if (loans.Count == 0)
                {
                    _logger.LogWarning("No loans returned for ContractId {ContractId} filters.", connection.ContractId);
                    return new HashSet<int>();
                }

                // Upsert handles cross-contract placement: if a loan already exists in
                // another contract it is moved rather than duplicated, and the migration
                // is written to ContractMigrationLog inside the same transaction.
                return await contractDataSvc.UpsertLoansAsync(
                    connection.ContractId,
                    connection.EntityId,
                    connection.CategoryId,
                    loans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data sync for ContractId {ContractId}.", connection.ContractId);
                return new HashSet<int>();
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Payments sync — fetches payment records directly from LMS API
        // using the date window since the last successful fetch.
        // ─────────────────────────────────────────────────────────────────────
        private async Task SyncPaymentsAsync(ContractLmsConnection connection, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var loanApiService  = scope.ServiceProvider.GetRequiredService<ILoanApiService>();
                var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();

                if (_cachedToken == null || DateTime.UtcNow >= _tokenExpiry)
                {
                    _logger.LogInformation("Requesting new API token for {Entity}...", connection.LmsEntityId);
                    _cachedToken = await loanApiService.GetTokenAsync(connection.LmsEntityId);

                    if (_cachedToken == null)
                    {
                        _logger.LogError("Failed to obtain API token. Skipping payments for ContractId {ContractId}.", connection.ContractId);
                        return;
                    }
                    _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
                }

                // Fetch payments since last successful fetch (default 30-day look-back on first run).
                var dateFrom = (DateTime)connection.LastPaymentsFetch;
                var dateTo   = DateTime.Now;

                _logger.LogInformation(
                    "Fetching payments for ContractId {ContractId} from {From:yyyy-MM-dd} to {To:yyyy-MM-dd}.",
                    connection.ContractId, dateFrom, dateTo);

                var freshPayments = await loanApiService.GetPaymentsAsync(_cachedToken, dateFrom, dateTo);

                await contractDataSvc.SyncPaymentsAsync(connection.ContractId, connection.EntityId, freshPayments, connection.CommissionRate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during payments sync for ContractId {ContractId}.", connection.ContractId);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Between-sync migration pass — returns count of records moved.
        // ─────────────────────────────────────────────────────────────────────
        private async Task<int> MigrateContractRecordsAsync(CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();
                return await contractDataSvc.MigrateRecordsAsync(_connections);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during contract record migration.");
                return 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Allocate agents for all categories touched this cycle.
        // ─────────────────────────────────────────────────────────────────────
        private async Task AllocateAffectedCategoriesAsync(
            Dictionary<int, int> categoryEntityMap,
            CancellationToken stoppingToken)
        {
            using var scope = _serviceProvider.CreateScope();
            var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();

            foreach (var (catId, entId) in categoryEntityMap)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    await contractDataSvc.AllocateContractsToAgentsAsync(catId, entId);
                    _logger.LogInformation("Allocation done for CategoryId {CatId}.", catId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Allocation failed for CategoryId {CatId}.", catId);
                }
            }
        }

        private async Task LoadConnectionsAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var contractService = scope.ServiceProvider.GetRequiredService<IContractService>();
                _connections = await contractService.GetContractConnectionsAsync();
                _logger.LogInformation("Loaded {Count} contract connection(s).", _connections.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load contract connections.");
                _connections = new List<ContractLmsConnection>();
            }
        }
    }
}
