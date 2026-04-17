using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConnectBoxService.Models;
using ConnectBoxService.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

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

            await LoadConnectionsAsync();

            if (_connections.Count == 0)
            {
                _logger.LogWarning("No ContractLmsConnections found. Worker will idle.");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var connection in _connections)
                    {
                        /// Due for data fetch
                        bool dataDue = connection.NextDataFetch.HasValue ? connection.NextDataFetch <= DateTime.Now : true;

                        if (dataDue)
                        {
                            _logger.LogInformation(
                                "Syncing ContractId {ContractId} ({Name}, every {Minutes} mins)...",
                                connection.ContractId,
                                connection.DataRefreshCycleName,
                                connection.DataRefreshCycleMinutes);

                            // --- CALL SYNC AND ALLOCATION ---
                            await SyncContractAsync(connection, stoppingToken);

                            _lastRunTimes[connection.ContractId] = DateTime.UtcNow;
                        }

                        /// Due for payments fetch
                        bool paymentsDue = connection.NextPaymentsFetch.HasValue ? connection.NextPaymentsFetch <= DateTime.Now : true;

                        if (paymentsDue)
                        {
                            _logger.LogInformation(
                               "Payments sync for ContractId {ContractId} ({Name}, every {Minutes} mins)...",
                               connection.ContractId,
                               connection.PaymentsRefreshCycleName,
                               connection.PaymentsRefreshCycleMinutes);

                            await SyncPaymentsAsync(connection, stoppingToken);
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
        // Payments sync  (mirrors SyncContractAsync structure)
        // ─────────────────────────────────────────────────────────────────────
        private async Task SyncPaymentsAsync(ContractLmsConnection connection, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var loanApiService = scope.ServiceProvider.GetRequiredService<ILoanApiService>();
                var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();

                // Token (re-uses / refreshes the worker-level cache)
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

                // Fetch current loan snapshot from LMS
                var freshLoans = await loanApiService.GetLoansAsync(_cachedToken, connection);

                if (freshLoans.Count == 0)
                {
                    _logger.LogWarning("No loans returned for payments sync, ContractId {ContractId}.", connection.ContractId);
                    return;
                }

                // Detect payments via OutSourcedAmount delta & persist
                await contractDataSvc.SyncPaymentsAsync(connection.ContractId,connection.EntityId, freshLoans);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during payments sync for ContractId {ContractId}.", connection.ContractId);
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

        private async Task SyncContractAsync(ContractLmsConnection connection, CancellationToken stoppingToken)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var loanApiService = scope.ServiceProvider.GetRequiredService<ILoanApiService>();
                var contractDataSvc = scope.ServiceProvider.GetRequiredService<IContractDataService>();

                // ── Step 1: Get token ─────────────────────────────────────
                if (_cachedToken == null || DateTime.UtcNow >= _tokenExpiry)
                {
                    _logger.LogInformation("Requesting new API token for {Entity}...", connection.LmsEntityId);
                    _cachedToken = await loanApiService.GetTokenAsync(connection.LmsEntityId);

                    if (_cachedToken == null)
                    {
                        _logger.LogError("Failed to obtain API token. Skipping ContractId {ContractId}.", connection.ContractId);
                        return;
                    }

                    _tokenExpiry = DateTime.UtcNow.AddMinutes(55);
                }

                // ── Step 2: Fetch loans ───────────────────────────────────
                var loans = await loanApiService.GetLoansAsync(_cachedToken, connection);

                _logger.LogInformation(
                    "Fetched {Count} loans for ContractId {ContractId} (EntityId: {EntityId}).",
                    loans.Count, connection.ContractId, connection.LmsEntityId);

                if (loans.Count == 0)
                {
                    _logger.LogWarning("No loans returned for ContractId {ContractId} filters.", connection.ContractId);
                    return;
                }

                // ── Step 3: Upsert into ContractData ──────────────────────
                await contractDataSvc.UpsertLoansAsync(
                    connection.ContractId,
                    connection.EntityId,
                    connection.CategoryId,
                    loans);

                // ── Step 4: Allocate to Agents Equally (Amount & Count) ──
                _logger.LogInformation("Allocating {Count} records to agents for Category {CategoryId}...", loans.Count, connection.CategoryId);

                // Parsing to int as required by the Allocate method signature
                if (int.TryParse(connection.CategoryId, out int catId) && int.TryParse(connection.EntityId, out int entId))
                {
                    await contractDataSvc.AllocateContractsToAgentsAsync(catId, entId);
                    _logger.LogInformation("Allocation successful for ContractId {ContractId}.", connection.ContractId);
                }
                else
                {
                    _logger.LogError("Failed to parse CategoryId or EntityId for allocation. ContractId: {ContractId}", connection.ContractId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Sync/Allocation for ContractId {ContractId}.", connection.ContractId);
            }
        }
    }
}