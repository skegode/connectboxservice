using Microsoft.Data.SqlClient;
using ConnectBoxService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Transactions;

namespace ConnectBoxService.Services
{
    public class ContractDataService : IContractDataService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ContractDataService> _logger;

        public ContractDataService(IConfiguration config, ILogger<ContractDataService> logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Upserts loan records into ContractData.
        /// </summary>
        public async Task UpsertLoansAsync(int contractId, string entityId, string categoryId, List<LoanDto> loans)
        {
            if (loans == null || loans.Count == 0) return;

            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            int upserted = 0;

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var loan in loans)
                {
                    const string sql = """
                        MERGE INTO ContractData AS target
                        USING (SELECT @ContractId AS ContractId, @LoanId AS LoanId) AS source
                            ON target.ContractId = source.ContractId
                           AND target.LoanId     = source.LoanId
                        WHEN MATCHED THEN
                            UPDATE SET
                                FirstName        = @FirstName,
                                OtherNames       = @OtherNames,
                                PhoneNumber      = @PhoneNumber,
                                EmailAddrerss    = @EmailAddress,
                                IdOrPassport     = @IdOrPassport,
                                AmountDisbursed  = @AmountDisbursed,
                                Installments     = @Installments,
                                ArrearsAmount    = @ArrearsAmount,
                                DaysInArrears    = @DaysInArrears,
                                OLB              = @Olb,
                                Branch           = @Branch,
                                OutSourcedAmount = @OutSourcedAmount,
                                EntityId         = @EntityId,
                                CategoryId       = @CategoryId,
                                Penalty          = @Penalty,
                                Expectedduedate  = GETDATE(),
                                BorrowerRefId    = @BorrowerId,
                                LoanRefId        = @LoanId
                        WHEN NOT MATCHED THEN
                            INSERT (
                                ContractId, LoanId, FirstName, OtherNames, PhoneNumber,
                                EmailAddrerss, IdOrPassport, AmountDisbursed, Installments,
                                ArrearsAmount, DaysInArrears, OLB, Branch, OutSourcedAmount,
                                EntityId, CategoryId, Penalty, Expectedduedate, BorrowerRefId, LoanRefId
                            )
                            VALUES (
                                @ContractId, @LoanId, @FirstName, @OtherNames, @PhoneNumber,
                                @EmailAddress, @IdOrPassport, @AmountDisbursed, @Installments,
                                @ArrearsAmount, @DaysInArrears, @Olb, @Branch, @OutSourcedAmount,
                                @EntityId, @CategoryId, @Penalty, GETDATE(), @BorrowerId, @LoanId
                            );
                        """;

                    await using var cmd = new SqlCommand(sql, conn, transaction);
                    cmd.Parameters.AddWithValue("@ContractId", contractId);
                    cmd.Parameters.AddWithValue("@LoanId", loan.Id);
                    cmd.Parameters.AddWithValue("@BorrowerId", loan.borrowerId);
                    cmd.Parameters.AddWithValue("@FirstName", loan.FirstName ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@OtherNames", loan.OtherName ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@PhoneNumber", loan.PhoneNumber ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@EmailAddress", loan.EmailAddress ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@IdOrPassport", loan.NationalId ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@AmountDisbursed", loan.AmountToDisburse);
                    cmd.Parameters.AddWithValue("@Installments", loan.RepaymentPeriod);
                    cmd.Parameters.AddWithValue("@ArrearsAmount", loan.Arrears);
                    cmd.Parameters.AddWithValue("@DaysInArrears", loan.DaysInArrears);
                    cmd.Parameters.AddWithValue("@Olb", loan.LoanBalance);
                    cmd.Parameters.AddWithValue("@Branch", loan.Branch ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@OutSourcedAmount", loan.OutsourcedAmount);
                    cmd.Parameters.AddWithValue("@EntityId", entityId);
                    cmd.Parameters.AddWithValue("@CategoryId", categoryId);
                    cmd.Parameters.AddWithValue("@Penalty", loan.Penalty);

                    await cmd.ExecuteNonQueryAsync();
                    upserted++;
                }

                using var logCmd = new SqlCommand("INSERT INTOContractLmsLogs (ContractId,Records,CreatedDate) VALUES (@id,@records,GETDATE())", conn, transaction);
                logCmd.Parameters.AddWithValue("@id", contractId);
                logCmd.Parameters.AddWithValue("@records", upserted);
                await logCmd.ExecuteNonQueryAsync();

                using var upCmd = new SqlCommand("update ContractLmsConnections " +
                    "SET LastDataFetch=getdate(), " +
                    "NextDataFetch = DATEADD(MINUTE,(select RefreshCycles.DurationMinutes FROM RefreshCycles where RefreshCycles.id=ContractLmsConnections.DataRefreshCycle),getdate()) " +
                    "WHERE id=@id", conn, transaction);
                upCmd.Parameters.AddWithValue("@id", contractId);
                await upCmd.ExecuteNonQueryAsync();

                _logger.LogInformation("Upserted {Count} loan records for CategoryId {CategoryId}.", upserted, categoryId);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Failed to upsert loans for ContractId {ContractId}.", contractId);
                throw;
            }
        }

        /// <summary>
        /// Allocates unassigned records to agents equally by both Count and Amount (Greedy Algorithm).
        /// </summary>
        public async Task AllocateContractsToAgentsAsync(int categoryId, int orgId)
        {
            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // 1. Fetch available agents for this category
            var agents = new List<int>();
            using (var cmd = new SqlCommand("SELECT AgentId FROM CategoryAgents WHERE CategoryID = @cat", conn))
            {
                cmd.Parameters.AddWithValue("@cat", categoryId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) agents.Add(Convert.ToInt32(reader["AgentId"]));
            }

            if (agents.Count == 0) return;

            // 2. Fetch unassigned contracts sorted by OLB descending
            var contracts = new List<(int Id, decimal Olb)>();
            using (var cmd = new SqlCommand("SELECT Id, OLB FROM ContractData WHERE CategoryID = @cat AND (AssignedToAgent = 0 OR AssignedToAgent IS NULL) AND IsDeleted = 0 ORDER BY OLB DESC", conn))
            {
                cmd.Parameters.AddWithValue("@cat", categoryId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    contracts.Add((Convert.ToInt32(reader["Id"]), reader["OLB"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["OLB"])));
            }

            if (contracts.Count == 0) return;

            // 3. Greedy Balance Logic
            var agentPool = agents.Select(id => new { AgentId = id, TotalOlb = 0m, Records = new List<int>() }).ToList();

            foreach (var contract in contracts)
            {
                var bestAgent = agentPool.OrderBy(a => a.TotalOlb).First();
                bestAgent.Records.Add(contract.Id);

                // Update local pool state
                var idx = agentPool.FindIndex(x => x.AgentId == bestAgent.AgentId);
                agentPool[idx] = bestAgent with { TotalOlb = bestAgent.TotalOlb + contract.Olb };
            }

            // 4. Batch Update database
            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var ap in agentPool)
                {
                    if (ap.Records.Count == 0) continue;

                    // Update ContractData
                    foreach (var recordId in ap.Records)
                    {
                        using var upCmd = new SqlCommand("UPDATE ContractData SET AssignedAgent = @a, AssignedToAgent = 1, DateUpdated = GETDATE() WHERE Id = @id", conn, transaction);
                        upCmd.Parameters.AddWithValue("@a", ap.AgentId);
                        upCmd.Parameters.AddWithValue("@id", recordId);
                        await upCmd.ExecuteNonQueryAsync();
                    }

                    // Log Allocation Summary
                    using var logCmd = new SqlCommand("""
                        INSERT INTO Contractallocation (AgentId, ContractCount, ContractSummation, CategoryId, InitiatedDate)
                        VALUES (@a, @c, @s, @cat, GETDATE())
                        """, conn, transaction);
                    logCmd.Parameters.AddWithValue("@a", ap.AgentId);
                    logCmd.Parameters.AddWithValue("@c", ap.Records.Count);
                    logCmd.Parameters.AddWithValue("@s", ap.TotalOlb);
                    logCmd.Parameters.AddWithValue("@cat", categoryId);
                    await logCmd.ExecuteNonQueryAsync();
                }
                transaction.Commit();
                _logger.LogInformation("Balanced allocation completed for Category {CategoryId}.", categoryId);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Failed to allocate contracts for Category {CategoryId}.", categoryId);
                throw;
            }
        }

        /// <summary>
        /// Detects payments by comparing the current OutSourcedAmount on each LoanDto
        /// against what is stored in ContractData (matched via LoanRefId).
        /// A payment has occurred when the new API value is LESS than the stored value.
        ///
        /// For every detected payment:
        ///   • Updates ContractData  → OutSourcedAmount, AmountPaid, DateUpdated
        ///   • Inserts RepaymentHistory row with InitialOlb / AmountPaid / NewOlb
        /// </summary>
        public async Task SyncPaymentsAsync(int contractId, List<LoanDto> freshLoans)
        {
            if (freshLoans == null || freshLoans.Count == 0) return;

            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // ── 1. Pull existing ContractData rows for this contract ──────────────
            //    Keyed by LoanRefId so we can O(1) match against the API payload.
            var existingRows = new Dictionary<string, (int Id, int? AgentId, decimal StoredOlb, decimal StoredOutSourced, int? ptpid)>();

            const string fetchSql = """
            SELECT Id, LoanRefId, AssignedAgent, OLB, OutSourcedAmount, ptpid
            FROM   ContractData
            WHERE  ContractId = @contractId
              AND  IsDeleted  = 0
              AND  LoanRefId  IS NOT NULL
            """;

            await using (var fetchCmd = new SqlCommand(fetchSql, conn))
            {
                fetchCmd.Parameters.AddWithValue("@contractId", contractId);
                await using var reader = await fetchCmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    // LoanRefId stored same value as LoanId (see UpsertLoansAsync)
                    var loanRefId = reader["LoanRefId"]?.ToString();
                    if (string.IsNullOrWhiteSpace(loanRefId)) continue;

                    existingRows[loanRefId] = (
                        Id: Convert.ToInt32(reader["Id"]),
                        AgentId: reader["AssignedAgent"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["AssignedAgent"]),
                        StoredOlb: reader["OLB"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["OLB"]),
                        StoredOutSourced: reader["OutSourcedAmount"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["OutSourcedAmount"]),
                        ptpid: reader["ptpid"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["ptpid"])
                    );
                }
            }

            if (existingRows.Count == 0)
            {
                _logger.LogWarning("SyncPayments: No ContractData rows found for ContractId {ContractId}.", contractId);
                return;
            }

            // ── 2. Detect payments ────────────────────────────────────────────────
            var payments = new List<(int ContractDataId, int? AgentId, decimal InitialOlb, decimal AmountPaid, decimal NewOlb, decimal NewOutSourced, int? ptpid)>();

            foreach (var loan in freshLoans)
            {
                var loanKey = loan.Id?.ToString();
                if (string.IsNullOrWhiteSpace(loanKey)) continue;

                if (!existingRows.TryGetValue(loanKey, out var existing)) continue;

                decimal newOutSourced = loan.OutsourcedAmount;

                // Payment detected: the outsourced amount went DOWN
                if (newOutSourced < existing.StoredOutSourced)
                {
                    decimal amountPaid = existing.StoredOutSourced - newOutSourced;
                    decimal newOlb = loan.LoanBalance;           // fresh OLB from API

                    payments.Add((
                        ContractDataId: existing.Id,
                        AgentId: existing.AgentId,
                        InitialOlb: existing.StoredOlb,
                        AmountPaid: amountPaid,
                        NewOlb: newOlb,
                        NewOutSourced: newOutSourced,
                        ptpid: existing.ptpid
                    ));

                    _logger.LogInformation(
                        "Payment detected — ContractDataId {Id}: paid {Paid:C}, OutSourced {Old:C}→{New:C}",
                        existing.Id, amountPaid, existing.StoredOutSourced, newOutSourced);
                }
            }

            if (payments.Count == 0)
            {
                _logger.LogInformation("SyncPayments: No new payments detected for ContractId {ContractId}.", contractId);
                return;
            }

            // ── 3. Persist in a single transaction ───────────────────────────────
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var p in payments)
                {
                    // 3a. Update ContractData
                    const string updateSql = """
                    UPDATE ContractData
                    SET    OutSourcedAmount = @newOutSourced,
                           AmountRepaid       = ISNULL(AmountRepaid, 0) + @amountPaid,
                           OLB              = @newOlb,
                           DateUpdated      = GETDATE()
                    WHERE  Id = @id
                    """;

                    await using var upCmd = new SqlCommand(updateSql, conn, tx);
                    upCmd.Parameters.AddWithValue("@newOutSourced", p.NewOutSourced);
                    upCmd.Parameters.AddWithValue("@amountPaid", p.AmountPaid);
                    upCmd.Parameters.AddWithValue("@newOlb", p.NewOlb);
                    upCmd.Parameters.AddWithValue("@id", p.ContractDataId);
                    await upCmd.ExecuteNonQueryAsync();

                    if (p.ptpid.HasValue && p.ptpid > 0)
                    {
                        //// Update ptp table as well to reflect the payment (so it shows in the agent app)
                        string ptpUpdateSql = """
                            UPDATE PromisedToPay
                            SET AmountPaid = AmountPaid + @amountPaid, PromisedAmount = PromisedAmount - @amountPaid, Dateofpayment = GETDATE(), 
                            PaymentStatus = CASE 
                                WHEN PromisedAmount - @amountPaid <= 0 THEN 3
                                ELSE 2 
                            END, 
                            closed = CASE 
                                WHEN PromisedAmount - @amountPaid <= 0 THEN 1
                                ELSE 0 
                            END
                            WHERE  Id = @id
                            """;
                        await using var upPtpCmd = new SqlCommand(ptpUpdateSql, conn, tx);
                        upPtpCmd.Parameters.AddWithValue("@id", p.ptpid);
                        upPtpCmd.Parameters.AddWithValue("@amountPaid", p.AmountPaid);
                        await upPtpCmd.ExecuteNonQueryAsync();
                    }

                    // 3b. Insert RepaymentHistory
                    const string historySql = """
                    INSERT INTO RepaymentHistory
                        (AgentId, ContractId, ContractDataId, InitialOlb, AmountPaid, NewOlb)
                    VALUES
                        (@agentId, @contractId, @contractDataId, @initialOlb, @amountPaid, @newOlb)
                    """;

                    await using var histCmd = new SqlCommand(historySql, conn, tx);
                    histCmd.Parameters.AddWithValue("@agentId", p.AgentId.HasValue ? (object)p.AgentId.Value : DBNull.Value);
                    histCmd.Parameters.AddWithValue("@contractId", contractId);
                    histCmd.Parameters.AddWithValue("@contractDataId", p.ContractDataId);
                    histCmd.Parameters.AddWithValue("@initialOlb", p.InitialOlb);
                    histCmd.Parameters.AddWithValue("@amountPaid", p.AmountPaid);
                    histCmd.Parameters.AddWithValue("@newOlb", p.NewOlb);
                    await histCmd.ExecuteNonQueryAsync();
                }

                // 3c. Stamp the LastPaymentsFetch on the connection row
                const string stampSql = """
                UPDATE ContractLmsConnections
                SET    LastPaymentsFetch = GETDATE(),
                       NextPaymentsFetch = DATEADD(
                           MINUTE,
                           (SELECT DurationMinutes FROM RefreshCycles
                            WHERE  id = ContractLmsConnections.PaymentsRefreshCycle),
                           GETDATE())
                WHERE  ContractId = @contractId
                """;

                await using var stampCmd = new SqlCommand(stampSql, conn, tx);
                stampCmd.Parameters.AddWithValue("@contractId", contractId);
                await stampCmd.ExecuteNonQueryAsync();

                tx.Commit();

                _logger.LogInformation(
                    "SyncPayments: committed {Count} repayment record(s) for ContractId {ContractId}................................",
                    payments.Count, contractId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "SyncPayments: transaction rolled back for ContractId {ContractId}.", contractId);
                throw;
            }
        }

    }
}