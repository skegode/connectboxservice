using Microsoft.Data.SqlClient;
using ConnectBoxService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data;

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

                await using var cmd = new SqlCommand(sql, conn);
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

            _logger.LogInformation("Upserted {Count} loan records for CategoryId {CategoryId}.", upserted, categoryId);
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
    }
}