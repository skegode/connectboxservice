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
        /// Upserts loans into ContractData, matching on LoanId + Entityid across all contracts.
        /// If a loan already exists in a different contract (its days/amount moved it into a new
        /// band), the record is updated in place and migrated to the correct contract rather than
        /// creating a duplicate. Migration is logged and the agent assignment is cleared so the
        /// next allocation cycle re-distributes the record.
        ///
        /// Returns the set of integer category IDs that received new or migrated records so the
        /// caller can run AllocateContractsToAgentsAsync for each affected category.
        /// </summary>
        public async Task<HashSet<int>> UpsertLoansAsync(int contractId, string entityId, string categoryId, List<LoanDto> loans)
        {
            var affectedCategories = new HashSet<int>();
            if (loans == null || loans.Count == 0) return affectedCategories;

            int.TryParse(categoryId, out int targetCategoryId);

            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            int inserted = 0, updated = 0, migrated = 0;

            using var transaction = conn.BeginTransaction();
            try
            {
                foreach (var loan in loans)
                {
                    // ── 1. Find the record in ContractData by LoanId + Entityid (any contract) ──
                    int? existingRowId      = null;
                    int? existingContractId = null;
                    int? existingClosed = null;

                    const string checkSql = """SELECT TOP 1 Id, ContractId,IsDeleted,IsClosed FROM ContractData WHERE LoanId = @LoanId AND Entityid = @EntityId AND (IsDeleted=0 OR IsDeleted IS NULL)""";

                    await using (var checkCmd = new SqlCommand(checkSql, conn, transaction))
                    {
                        checkCmd.Parameters.AddWithValue("@LoanId",   loan.Id);
                        checkCmd.Parameters.AddWithValue("@EntityId", entityId);
                        await using var rdr = await checkCmd.ExecuteReaderAsync();
                        if (await rdr.ReadAsync())
                        {
                            existingRowId      = Convert.ToInt32(rdr["Id"]);
                            existingContractId = Convert.ToInt32(rdr["ContractId"]);
                            existingClosed     = rdr["IsClosed"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["IsClosed"]);
                        }
                    }

                    bool recordExists = existingRowId.HasValue;
                    bool isMigration  = recordExists && existingContractId!.Value != contractId;

                    if (recordExists)
                    {
                        // ── 2a. Update existing record.
                        //        If the contract changed, also update ContractId / CategoryId
                        //        and clear the agent assignment so re-allocation picks it up.  ──
                        string updateSql = isMigration
                            ? """
                                UPDATE ContractData
                                SET ContractId = @ContractId,
                                    CategoryID = @CategoryId,
                                    AssignedAgent = NULL,
                                    AssignedToAgent = 0,
                                    FirstName = @FirstName,
                                    OtherNames = @OtherNames,
                                    PhoneNumber = @PhoneNumber,
                                    EmailAddrerss = @EmailAddress,
                                    IdOrPassport = @IdOrPassport,
                                    PrimaryLocationLatitude = @Latitude,
                                    PrimaryLocationLongitude = @Longitude,
                                    AmountDisbursed = @AmountDisbursed,
                                    Installments = @Installments,
                                    ArrearsAmount = @ArrearsAmount,
                                    DaysInArrears = @DaysInArrears,
                                    OLB = @Olb,
                                    BranchId = @BranchId,
                                    Branch = @Branch,
                                    OutSourcedAmount = @OutSourcedAmount,
                                    InitialOutSourcedAmount = @OutSourcedAmount,
                                    Penalty = @Penalty,
                                    BorrowerRefId = @BorrowerId,
                                    LoanRefId = @LoanId,
                                    IsClosed = 0,
                                    DateUpdated = GETDATE()
                                WHERE Id = @Id
                                """
                            : """
                                UPDATE ContractData
                                SET FirstName = @FirstName,
                                    OtherNames = @OtherNames,
                                    PhoneNumber = @PhoneNumber,
                                    EmailAddrerss = @EmailAddress,
                                    IdOrPassport = @IdOrPassport,
                                    PrimaryLocationLatitude = @Latitude,
                                    PrimaryLocationLongitude = @Longitude,
                                    AmountDisbursed = @AmountDisbursed,
                                    Installments = @Installments,
                                    ArrearsAmount = @ArrearsAmount,
                                    DaysInArrears = @DaysInArrears,
                                    OLB = @Olb,
                                    BranchId = @BranchId,
                                    Branch = @Branch,
                                    OutSourcedAmount = @OutSourcedAmount,
                                    InitialOutSourcedAmount = @OutSourcedAmount,
                                    Penalty = @Penalty,
                                    BorrowerRefId = @BorrowerId,
                                    LoanRefId = @LoanId,
                                    DateUpdated = GETDATE()
                                WHERE Id = @Id
                                """;

                        await using var upCmd = new SqlCommand(updateSql, conn, transaction);
                        upCmd.Parameters.AddWithValue("@Id",              existingRowId!.Value);
                        upCmd.Parameters.AddWithValue("@ContractId",      contractId);
                        upCmd.Parameters.AddWithValue("@CategoryId",      targetCategoryId > 0 ? (object)targetCategoryId : DBNull.Value);
                        upCmd.Parameters.AddWithValue("@LoanId",          loan.Id);
                        upCmd.Parameters.AddWithValue("@BorrowerId",      (object?)loan.borrowerId ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@FirstName",       (object?)loan.FirstName    ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@OtherNames",      (object?)loan.OtherName    ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@PhoneNumber",     (object?)loan.PhoneNumber  ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@EmailAddress",    (object?)loan.EmailAddress ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@IdOrPassport",    (object?)loan.NationalId   ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@Latitude",        (object?)loan.Latitude ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@Longitude",       (object?)loan.Longitude ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@AmountDisbursed", loan.AmountToDisburse);
                        upCmd.Parameters.AddWithValue("@Installments",    loan.RepaymentPeriod);
                        upCmd.Parameters.AddWithValue("@ArrearsAmount",   loan.Arrears);
                        upCmd.Parameters.AddWithValue("@DaysInArrears",   loan.DaysInArrears);
                        upCmd.Parameters.AddWithValue("@Olb",             loan.LoanBalance);
                        upCmd.Parameters.AddWithValue("@BranchId",          (object?)loan.BranchId ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@Branch",          (object?)loan.Branch ?? DBNull.Value);
                        upCmd.Parameters.AddWithValue("@OutSourcedAmount",loan.OutsourcedAmount);
                        upCmd.Parameters.AddWithValue("@Penalty",         loan.Penalty);
                        await upCmd.ExecuteNonQueryAsync();
                        updated++;

                        if (isMigration)
                        {
                            await WriteMigrationLogAsync(conn, transaction,
                                contractDataId: existingRowId.Value,
                                loanRefId:      loan.Id,
                                fromContractId: existingContractId!.Value,
                                toContractId:   contractId,
                                amount:         loan.AmountToDisburse,
                                days:           loan.DaysInArrears,
                                arrears:        loan.Arrears,
                                olb:            loan.LoanBalance,
                                reason:         $"LMS sync placed loan in ContractId {contractId} (previously ContractId {existingContractId.Value})");

                            migrated++;
                            if (targetCategoryId > 0) affectedCategories.Add(targetCategoryId);

                            _logger.LogInformation(
                                "UpsertLoans: LoanId {LoanId} moved ContractId {From} → {To}.",
                                loan.Id, existingContractId.Value, contractId);
                        }
                    }
                    else
                    {
                        // ── 2b. New loan — insert into the target contract ──
                        const string insertSql = """
                            INSERT INTO ContractData
                                (ContractId, LoanId, FirstName, OtherNames, PhoneNumber,
                                 EmailAddrerss, IdOrPassport, PrimaryLocationLatitude, PrimaryLocationLongitude, AmountDisbursed, Installments,
                                 ArrearsAmount, DaysInArrears, OLB, BranchId, Branch,
                                 OutSourcedAmount, InitialOutSourcedAmount,
                                 Entityid, CategoryID, Penalty, Expectedduedate, BorrowerRefId, LoanRefId)
                            VALUES
                                (@ContractId, @LoanId, @FirstName, @OtherNames, @PhoneNumber,
                                 @EmailAddress, @IdOrPassport, @Latitude, @Longitude, @AmountDisbursed, @Installments,
                                 @ArrearsAmount, @DaysInArrears, @Olb, @BranchId, @Branch,
                                 @OutSourcedAmount, @OutSourcedAmount,
                                 @EntityId, @CategoryId, @Penalty, GETDATE(), @BorrowerId, @LoanId)
                            """;

                        await using var insCmd = new SqlCommand(insertSql, conn, transaction);
                        insCmd.Parameters.AddWithValue("@ContractId",      contractId);
                        insCmd.Parameters.AddWithValue("@LoanId",          loan.Id);
                        insCmd.Parameters.AddWithValue("@BorrowerId",      (object?)loan.borrowerId  ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@FirstName",       (object?)loan.FirstName   ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@OtherNames",      (object?)loan.OtherName   ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@PhoneNumber",     (object?)loan.PhoneNumber ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@EmailAddress",    (object?)loan.EmailAddress ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@IdOrPassport",    (object?)loan.NationalId  ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@Latitude",        (object?)loan.Latitude ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@Longitude",       (object?)loan.Longitude ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@AmountDisbursed", loan.AmountToDisburse);
                        insCmd.Parameters.AddWithValue("@Installments",    loan.RepaymentPeriod);
                        insCmd.Parameters.AddWithValue("@ArrearsAmount",   loan.Arrears);
                        insCmd.Parameters.AddWithValue("@DaysInArrears",   loan.DaysInArrears);
                        insCmd.Parameters.AddWithValue("@Olb",             loan.LoanBalance);
                        insCmd.Parameters.AddWithValue("@BranchId",        loan.BranchId);
                        insCmd.Parameters.AddWithValue("@Branch",          (object?)loan.Branch ?? DBNull.Value);
                        insCmd.Parameters.AddWithValue("@OutSourcedAmount",loan.OutsourcedAmount);
                        insCmd.Parameters.AddWithValue("@EntityId",        entityId);
                        insCmd.Parameters.AddWithValue("@CategoryId",      targetCategoryId > 0 ? (object)targetCategoryId : DBNull.Value);
                        insCmd.Parameters.AddWithValue("@Penalty",         loan.Penalty);
                        await insCmd.ExecuteNonQueryAsync();

                        inserted++;
                        if (targetCategoryId > 0) affectedCategories.Add(targetCategoryId);
                    }
                }

                // ── Sync log ──
                await using var logCmd = new SqlCommand(
                    "INSERT INTO ContractLmsLogs (ContractId, Records, CreatedDate) VALUES (@id, @records, GETDATE())",
                    conn, transaction);
                logCmd.Parameters.AddWithValue("@id",      contractId);
                logCmd.Parameters.AddWithValue("@records", inserted + updated);
                await logCmd.ExecuteNonQueryAsync();

                // ── Stamp fetch timestamps ──
                await using var stampCmd = new SqlCommand("""
                    UPDATE ContractLmsConnections
                    SET LastDataFetch = GETDATE(),
                        NextDataFetch = DATEADD(MINUTE,
                            (SELECT DurationMinutes FROM RefreshCycles
                             WHERE  id = ContractLmsConnections.DataRefreshCycle),
                            GETDATE())
                    WHERE ContractId = @id
                    """, conn, transaction);
                stampCmd.Parameters.AddWithValue("@id", contractId);
                await stampCmd.ExecuteNonQueryAsync();

                transaction.Commit();

                _logger.LogInformation(
                    "UpsertLoans: ContractId {ContractId} — {Ins} inserted, {Upd} updated, {Mig} migrated.",
                    contractId, inserted, updated, migrated);

                return affectedCategories;
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Failed to upsert loans for ContractId {ContractId}.", contractId);
                throw;
            }
        }

        /// <summary>
        /// Allocates unassigned records to agents using weighted round-robin with branch filtering.
        /// Collection agents write to AssignedAgent; recovery agents write to RecoveryAgentId.
        /// An agent with no allowed branches can handle any branch; otherwise only records whose
        /// BranchId appears in the agent's Branches list are eligible.
        /// </summary>
        public async Task AllocateContractsToAgentsAsync(int categoryId, int orgId)
        {
            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            // 1. Fetch agents with type and branch restrictions
            var allAgents = new List<(int AgentId, string AgentName, List<int> AllowedBranches, string AgentType)>();
            using (var cmd = new SqlCommand(
                "SELECT AgentId, AgentName, ISNULL(Branches,'') AS Branches, ISNULL(AgentType,'Collection') AS AgentType FROM CategoryAgents WHERE CategoryID = @cat",
                conn))
            {
                cmd.Parameters.AddWithValue("@cat", categoryId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var branches = reader["Branches"].ToString()!;
                    var allowed = string.IsNullOrWhiteSpace(branches)
                        ? new List<int>()
                        : branches.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(b => b.Trim())
                                  .Where(b => int.TryParse(b, out _))
                                  .Select(int.Parse)
                                  .ToList();
                    allAgents.Add((
                        Convert.ToInt32(reader["AgentId"]),
                        reader["AgentName"].ToString()!,
                        allowed,
                        reader["AgentType"].ToString()!));
                }
            }

            if (allAgents.Count == 0) return;

            var collectionAgents = allAgents.Where(a => a.AgentType != "Recovery").ToList();
            var recoveryAgents   = allAgents.Where(a => a.AgentType == "Recovery").ToList();

            using var transaction = conn.BeginTransaction();
            try
            {
                // 2a. Assign collection agents to records not yet assigned
                if (collectionAgents.Count > 0)
                {
                    var records = new List<(int Id, string LoanId, decimal Olb, int? BranchId)>();
                    using (var cmd = new SqlCommand(
                        "SELECT Id, LoanId, OLB, BranchId FROM ContractData WHERE CategoryID = @cat AND (AssignedToAgent = 0 OR AssignedToAgent IS NULL) AND (IsDeleted = 0 OR IsDeleted IS NULL) ORDER BY OLB DESC",
                        conn, transaction))
                    {
                        cmd.Parameters.AddWithValue("@cat", categoryId);
                        using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                            records.Add((
                                Convert.ToInt32(reader["Id"]),
                                reader["LoanId"]?.ToString() ?? "",
                                reader["OLB"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["OLB"]),
                                reader["BranchId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["BranchId"])
                            ));
                    }

                    if (records.Count > 0)
                        await RunRoundRobinAsync(conn, transaction, collectionAgents, records,
                            categoryId, orgId, isRecovery: false);
                }

                // 2b. Assign recovery agents to records not yet assigned a recovery agent
                if (recoveryAgents.Count > 0)
                {
                    var records = new List<(int Id, string LoanId, decimal Olb, int? BranchId)>();
                    using (var cmd = new SqlCommand(
                        "SELECT Id, LoanId, OLB, BranchId FROM ContractData WHERE CategoryID = @cat AND RecoveryAgentId IS NULL AND (IsDeleted = 0 OR IsDeleted IS NULL) ORDER BY OLB DESC",
                        conn, transaction))
                    {
                        cmd.Parameters.AddWithValue("@cat", categoryId);
                        using var reader = await cmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                            records.Add((
                                Convert.ToInt32(reader["Id"]),
                                reader["LoanId"]?.ToString() ?? "",
                                reader["OLB"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["OLB"]),
                                reader["BranchId"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["BranchId"])
                            ));
                    }

                    if (records.Count > 0)
                        await RunRoundRobinAsync(conn, transaction, recoveryAgents, records,
                            categoryId, orgId, isRecovery: true);
                }

                transaction.Commit();
                _logger.LogInformation("Allocation completed for Category {CategoryId}.", categoryId);
            }
            catch (Exception ex)
            {
                transaction.Rollback();
                _logger.LogError(ex, "Failed to allocate contracts for Category {CategoryId}.", categoryId);
                throw;
            }
        }

        private async Task RunRoundRobinAsync(
            SqlConnection conn,
            SqlTransaction transaction,
            List<(int AgentId, string AgentName, List<int> AllowedBranches, string AgentType)> agents,
            List<(int Id, string LoanId, decimal Olb, int? BranchId)> records,
            int categoryId, int orgId, bool isRecovery)
        {
            decimal totalOlb = records.Sum(r => r.Olb);

            // Track per-agent state for weighted round-robin scoring (same formula as the API)
            var pool = agents.Select(a => new
            {
                a.AgentId,
                a.AllowedBranches,
                a.AgentType,
                AssignedCount = 0,
                AssignedOlb   = 0m,
                CurrentScore  = 0m,
                Records       = new List<(int Id, string LoanId, decimal Olb)>()
            }).ToList();

            foreach (var record in records)
            {
                var best = pool
                    .Where(a => !a.AllowedBranches.Any() ||
                                (record.BranchId.HasValue && a.AllowedBranches.Contains(record.BranchId.Value)))
                    .OrderBy(a => a.CurrentScore)
                    .FirstOrDefault();

                if (best == null) continue;

                best.Records.Add((record.Id, record.LoanId, record.Olb));

                var idx     = pool.FindIndex(x => x.AgentId == best.AgentId);
                int newCnt  = best.AssignedCount + 1;
                decimal newOlb = best.AssignedOlb + record.Olb;
                pool[idx] = best with
                {
                    AssignedCount = newCnt,
                    AssignedOlb   = newOlb,
                    CurrentScore  = newCnt + (newOlb / (totalOlb + 1))
                };
            }

            string agentTypeLabel = isRecovery ? "Recovery" : "Collection";

            foreach (var ap in pool)
            {
                if (ap.Records.Count == 0) continue;

                foreach (var (recordId, loanId, olb) in ap.Records)
                {
                    // Update the appropriate agent field on ContractData
                    string updateSql = isRecovery
                        ? "UPDATE ContractData SET RecoveryAgentId = @agentId, DateUpdated = GETDATE() WHERE Id = @id"
                        : "UPDATE ContractData SET AssignedAgent = @agentId, AssignedToAgent = 1, DateUpdated = GETDATE() WHERE Id = @id";

                    using var upCmd = new SqlCommand(updateSql, conn, transaction);
                    upCmd.Parameters.AddWithValue("@agentId", ap.AgentId);
                    upCmd.Parameters.AddWithValue("@id",      recordId);
                    await upCmd.ExecuteNonQueryAsync();

                    // Assignment log (aligned with API schema)
                    using var logCmd = new SqlCommand("""
                        INSERT INTO CategoryAssignmentLog
                            (CategoryID, AgentId, ContractDataId, LoanID, AssignedAmount, BusyScore, EntityId, AgentType)
                        VALUES
                            (@categoryId, @agentId, @recordId, @loanId, @olb, @score, @entityId, @agentType)
                        """, conn, transaction);
                    logCmd.Parameters.AddWithValue("@categoryId", categoryId);
                    logCmd.Parameters.AddWithValue("@agentId",    ap.AgentId);
                    logCmd.Parameters.AddWithValue("@recordId",   recordId);
                    logCmd.Parameters.AddWithValue("@loanId",     string.IsNullOrEmpty(loanId) ? (object)DBNull.Value : loanId);
                    logCmd.Parameters.AddWithValue("@olb",        olb);
                    logCmd.Parameters.AddWithValue("@score",      ap.CurrentScore);
                    logCmd.Parameters.AddWithValue("@entityId",   orgId);
                    logCmd.Parameters.AddWithValue("@agentType",  agentTypeLabel);
                    await logCmd.ExecuteNonQueryAsync();
                }

                // Allocation summary log
                using var summaryCmd = new SqlCommand("""
                    INSERT INTO Contractallocation (AgentId, ContractCount, ContractSummation, CategoryId, InitiatedDate)
                    VALUES (@agentId, @count, @total, @categoryId, GETDATE())
                    """, conn, transaction);
                summaryCmd.Parameters.AddWithValue("@agentId",     ap.AgentId);
                summaryCmd.Parameters.AddWithValue("@count",       ap.Records.Count);
                summaryCmd.Parameters.AddWithValue("@total",       ap.AssignedOlb);
                summaryCmd.Parameters.AddWithValue("@categoryId",  categoryId);
                await summaryCmd.ExecuteNonQueryAsync();
            }

            _logger.LogInformation(
                "RunRoundRobin ({Type}): assigned {Count} record(s) across {Agents} agent(s) for Category {CategoryId}.",
                agentTypeLabel, pool.Sum(a => a.Records.Count), pool.Count(a => a.Records.Count > 0), categoryId);
        }

        /// <summary>
        /// Fetches payments directly from the LMS payments API (not OLB comparison).
        ///
        /// For each payment record:
        ///   • Matches to ContractData by AccountNo = LoanRefId.
        ///   • Finds the open PTP whose window (CreatedDate → PromisedDate) contains the payment date.
        ///   • Accumulates AmountPaid per PTP; closes PTP when total paid ≥ PromisedAmount.
        ///   • Updates ContractData OutSourcedAmount / AmountRepaid; sets IsClosed when fully paid.
        ///   • Inserts RepaymentHistory row for every ContractData row that received payments.
        /// </summary>
        public async Task SyncPaymentsAsync(int contractId, string EntityId, List<LmsPaymentDto> freshPayments, decimal commissionRate)
        {
            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            if (freshPayments == null || freshPayments.Count == 0)
            {
                _logger.LogInformation("SyncPayments: No new payments from LMS for ContractId {ContractId}.", contractId);
                await StampPaymentsFetchAsync(conn, contractId);
                return;
            }

            // ── 1. Load ContractData rows keyed by LoanRefId ─────────────────────
            var existingRows = new Dictionary<string, (int Id, decimal Olb, decimal OutSourced, string? LoanId)>();

            const string fetchRowsSql = """
                SELECT Id, LoanRefId, LoanID, OLB, OutSourcedAmount
                FROM   ContractData
                WHERE  ContractId = @contractId
                  AND  IsDeleted  = 0
                  AND  LoanRefId  IS NOT NULL
                """;

            await using (var cmd = new SqlCommand(fetchRowsSql, conn))
            {
                cmd.Parameters.AddWithValue("@contractId", contractId);
                await using var rdr = await cmd.ExecuteReaderAsync();
                while (await rdr.ReadAsync())
                {
                    var loanRefId = rdr["LoanRefId"]?.ToString();
                    if (string.IsNullOrWhiteSpace(loanRefId)) continue;
                    existingRows[loanRefId] = (
                        Id:        Convert.ToInt32(rdr["Id"]),
                        Olb:       rdr["OLB"]             == DBNull.Value ? 0m : Convert.ToDecimal(rdr["OLB"]),
                        OutSourced:rdr["OutSourcedAmount"] == DBNull.Value ? 0m : Convert.ToDecimal(rdr["OutSourcedAmount"]),
                        LoanId:    rdr["LoanID"]?.ToString()
                    );
                }
            }

            if (existingRows.Count == 0)
            {
                _logger.LogWarning("SyncPayments: No ContractData rows for ContractId {ContractId}.", contractId);
                await StampPaymentsFetchAsync(conn, contractId);
                return;
            }

            var recordIdsCsv = string.Join(",", existingRows.Values.Select(r => r.Id));

            // ── 2. Load open PTPs for all records in this contract ───────────────
            var openPtps = new Dictionary<int, List<(int PtpId, decimal PromisedAmount, DateTime CreatedDate, DateTime PromisedDate)>>();

            var fetchPtpsSql = $"""
                SELECT ID, RecordID, PromisedAmount, CreatedDate, PromisedDate
                FROM   PromisedToPay
                WHERE  RecordID IN ({recordIdsCsv})
                  AND  (closed = 0 OR closed IS NULL)
                """;

            await using (var ptpCmd = new SqlCommand(fetchPtpsSql, conn))
            await using (var ptpRdr = await ptpCmd.ExecuteReaderAsync())
            {
                while (await ptpRdr.ReadAsync())
                {
                    int recId = Convert.ToInt32(ptpRdr["RecordID"]);
                    if (!openPtps.ContainsKey(recId)) openPtps[recId] = new();
                    openPtps[recId].Add((
                        PtpId:          Convert.ToInt32(ptpRdr["ID"]),
                        PromisedAmount: Convert.ToDecimal(ptpRdr["PromisedAmount"]),
                        CreatedDate:    Convert.ToDateTime(ptpRdr["CreatedDate"]),
                        PromisedDate:   Convert.ToDateTime(ptpRdr["PromisedDate"])
                    ));
                }
            }

            // ── 3. Load CategoryAssignmentLog for all records — used to resolve
            //       the agent who held each record at the time of each payment.
            //       Grouped by ContractDataId, ordered ascending so we can walk them.
            var assignmentLog = new Dictionary<int, List<(int AgentId, DateTime AssignedDate)>>();

            var fetchAssignSql = $"""
                SELECT ContractDataId, AgentId, AssignedDate
                FROM   CategoryAssignmentLog
                WHERE  ContractDataId IN ({recordIdsCsv})
                  AND  EntityId = @entityId
                ORDER BY ContractDataId, AssignedDate
                """;

            await using (var assignCmd = new SqlCommand(fetchAssignSql, conn))
            {
                assignCmd.Parameters.AddWithValue("@entityId", int.TryParse(EntityId, out int entIdInt) ? (object)entIdInt : EntityId);
                await using var assignRdr = await assignCmd.ExecuteReaderAsync();
                while (await assignRdr.ReadAsync())
                {
                    int cdId = Convert.ToInt32(assignRdr["ContractDataId"]);
                    if (!assignmentLog.ContainsKey(cdId)) assignmentLog[cdId] = new();
                    assignmentLog[cdId].Add((
                        AgentId:      Convert.ToInt32(assignRdr["AgentId"]),
                        AssignedDate: Convert.ToDateTime(assignRdr["AssignedDate"])
                    ));
                }
            }

            int? GetAgentAtDate(int cdId, DateTime paymentDate)
            {
                if (!assignmentLog.TryGetValue(cdId, out var entries)) return null;
                // Last assignment on or before the payment date
                int? agent = null;
                foreach (var e in entries)
                {
                    if (e.AssignedDate <= paymentDate) agent = e.AgentId;
                    else break;
                }

                return agent;
            }

            // ── 4. Deduplicate: skip any payment whose MpesaRef is already recorded
            var incomingRefs = freshPayments
                .Where(p => !string.IsNullOrWhiteSpace(p.MpesaRef))
                .Select(p => p.MpesaRef)
                .Distinct()
                .ToList();

            var existingRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (incomingRefs.Count > 0)
            {
                var refsCsv = string.Join(",", incomingRefs.Select(r => $"'{r.Replace("'", "''")}'"));
                var dupSql  = $"""
                    SELECT PaymentReference
                    FROM   RepaymentHistory
                    WHERE  PaymentReference IN ({refsCsv})
                      AND  Entityid = @entityId
                    """;
                await using var dupCmd = new SqlCommand(dupSql, conn);
                dupCmd.Parameters.AddWithValue("@entityId", EntityId);
                await using var dupRdr = await dupCmd.ExecuteReaderAsync();
                while (await dupRdr.ReadAsync())
                    existingRefs.Add(dupRdr["PaymentReference"].ToString()!);
            }

            // ── 5. Match payments to ContractData rows; build per-payment history
            //       rows and per-record aggregated totals for ContractData update.
            var ptpTotals  = new Dictionary<int, decimal>();
            var cdTotals   = new Dictionary<int, (decimal InitialOutSourced, decimal TotalPaid)>();
            // Running per-record OLB so consecutive payments within one batch reflect each other.
            var runningOlb = existingRows.ToDictionary(kv => kv.Value.Id, kv => kv.Value.OutSourced);

            // Individual rows queued for RepaymentHistory INSERT.
            var historyRows = new List<(
                int    CdId,
                string? LoanId,
                int   AgentId,
                decimal InitialOlb,
                decimal AmountPaid,
                decimal NewOlb,
                DateTime PaymentDate,
                string? MpesaRef
            )>();

            var matchedPayments = freshPayments
                .Where(p => p.LoanId.HasValue && existingRows.ContainsKey(p.LoanId.Value.ToString()))
                .ToList();

            if (matchedPayments.Count == 0)
                _logger.LogInformation("SyncPayments: No payments matched ContractData for ContractId {ContractId} — will still close expired PTPs.", contractId);

            foreach (var payment in matchedPayments)
            {
                // Skip already-recorded MpesaRefs (idempotency)
                if (!string.IsNullOrWhiteSpace(payment.MpesaRef) && existingRefs.Contains(payment.MpesaRef))
                {
                    _logger.LogDebug("SyncPayments: skipping duplicate MpesaRef {Ref}.", payment.MpesaRef);
                    continue;
                }

                var row     = existingRows[payment.LoanId.Value.ToString()];
                var ptps    = openPtps.TryGetValue(row.Id, out var ptpList) ? ptpList : new();
                int? agentId = GetAgentAtDate(row.Id, payment.TransactedDate);
                agentId = agentId.HasValue ? agentId : 0;

                decimal initOlb = runningOlb[row.Id];
                decimal newOlb  = Math.Max(0m, initOlb - payment.Amount);
                runningOlb[row.Id] = newOlb;

                // Queue RepaymentHistory row
                historyRows.Add((row.Id, row.LoanId, (int)agentId, initOlb, payment.Amount, newOlb, payment.TransactedDate, payment.MpesaRef));

                // Accumulate totals for ContractData update
                if (!cdTotals.ContainsKey(row.Id))
                    cdTotals[row.Id] = (row.OutSourced, 0m);
                var (initOut, prevPaid) = cdTotals[row.Id];
                cdTotals[row.Id] = (initOut, prevPaid + payment.Amount);

                // PTP matching by transaction date window
                var ptpMatch = ptps.FirstOrDefault(p =>
                    payment.TransactedDate >= p.CreatedDate &&
                    payment.TransactedDate <= p.PromisedDate);
                if (ptpMatch.PtpId > 0)
                    ptpTotals[ptpMatch.PtpId] = ptpTotals.GetValueOrDefault(ptpMatch.PtpId) + payment.Amount;
            }

            // ── 6. Persist everything in one transaction ─────────────────────────
            using var tx = conn.BeginTransaction();
            try
            {
                // 6a. Update ContractData (one update per record, aggregated total)
                foreach (var (cdId, (initOut, totalPaid)) in cdTotals)
                {
                    decimal newOutSourced = Math.Max(0m, initOut - totalPaid);
                    bool    fullyClosed   = newOutSourced <= 0m;

                    var updateSql = fullyClosed
                        ? """
                            UPDATE ContractData
                            SET OutSourcedAmount = @outSourced,
                                AmountRepaid     = ISNULL(AmountRepaid, 0) + @paid,
                                lastPaymentDate  = @lastPay,
                                IsClosed         = 1,
                                DateUpdated      = GETDATE()
                            WHERE Id = @id
                            """
                        : """
                            UPDATE ContractData
                            SET OutSourcedAmount = @outSourced,
                                AmountRepaid     = ISNULL(AmountRepaid, 0) + @paid,
                                lastPaymentDate  = @lastPay,
                                DateUpdated      = GETDATE()
                            WHERE Id = @id
                            """;

                    await using var upCmd = new SqlCommand(updateSql, conn, tx);
                    upCmd.Parameters.AddWithValue("@outSourced", newOutSourced);
                    upCmd.Parameters.AddWithValue("@paid",       totalPaid);
                    upCmd.Parameters.AddWithValue("@lastPay",    historyRows.Where(h => h.CdId == cdId).Max(h => h.PaymentDate));
                    upCmd.Parameters.AddWithValue("@id",         cdId);
                    await upCmd.ExecuteNonQueryAsync();

                    if (fullyClosed)
                        _logger.LogInformation("SyncPayments: ContractDataId {Id} fully paid — marked IsClosed.", cdId);
                }

                // 6b. Insert one RepaymentHistory row per individual payment;
                //     also insert AgentCommissions row when a payment is mapped to an agent.
                const string historySql = """
                    INSERT INTO RepaymentHistory
                        (ContractId, ContractDataId, LoanID,
                         AgentId, InitialOlb, AmountPaid, NewOlb,
                         PaymentDate, PaymentReference, CommissionAmount, Entityid, CreatedDate)
                    VALUES
                        (@contractId, @cdId, @loanId,
                         @agentId, @initialOlb, @paid, @newOlb,
                         @paymentDate, @paymentRef, @commissionAmount, @entityId, GETDATE())
                    """;

                int commissionsInserted = 0;
                foreach (var h in historyRows)
                {
                    decimal commissionAmount = 0;
                    if (h.AgentId > 0 && commissionRate > 0m)
                    {
                        commissionAmount = Math.Round(h.AmountPaid * (commissionRate / 100m), 2);
                        commissionsInserted++;
                    }

                    await using var histCmd = new SqlCommand(historySql, conn, tx);
                    histCmd.Parameters.AddWithValue("@contractId",  contractId);
                    histCmd.Parameters.AddWithValue("@cdId",        h.CdId);
                    histCmd.Parameters.AddWithValue("@loanId",      h.LoanId);
                    histCmd.Parameters.AddWithValue("@agentId",     h.AgentId);
                    histCmd.Parameters.AddWithValue("@initialOlb",  h.InitialOlb);
                    histCmd.Parameters.AddWithValue("@paid",        h.AmountPaid);
                    histCmd.Parameters.AddWithValue("@newOlb",      h.NewOlb);
                    histCmd.Parameters.AddWithValue("@paymentDate", h.PaymentDate);
                    histCmd.Parameters.AddWithValue("@paymentRef",  string.IsNullOrWhiteSpace(h.MpesaRef) ? (object)DBNull.Value : h.MpesaRef);
                    histCmd.Parameters.AddWithValue("@commissionAmount", commissionAmount);
                    histCmd.Parameters.AddWithValue("@entityId",    EntityId);
                    await histCmd.ExecuteNonQueryAsync();
                }

                // 6c. Update PTPs matched by payment date window
                foreach (var (ptpId, newlyPaid) in ptpTotals)
                {
                    const string ptpSql = """
                        UPDATE PromisedToPay
                        SET AmountPaid    = ISNULL(AmountPaid, 0) + @paid,
                            Dateofpayment = GETDATE(),
                            PaymentStatus = CASE
                                WHEN ISNULL(AmountPaid, 0) + @paid >= PromisedAmount THEN 3
                                ELSE 2
                            END,
                            closed = CASE
                                WHEN ISNULL(AmountPaid, 0) + @paid >= PromisedAmount THEN 1
                                ELSE 0
                            END
                        WHERE ID = @ptpId
                        """;
                    await using var ptpCmd = new SqlCommand(ptpSql, conn, tx);
                    ptpCmd.Parameters.AddWithValue("@paid",  newlyPaid);
                    ptpCmd.Parameters.AddWithValue("@ptpId", ptpId);
                    await ptpCmd.ExecuteNonQueryAsync();
                }

                // 6d. Close expired PTPs
                var expiredPtpSql = $"""
                    UPDATE PromisedToPay
                    SET closed        = 1,
                        PaymentStatus = CASE
                            WHEN ISNULL(AmountPaid, 0) >= PromisedAmount THEN 3
                            WHEN ISNULL(AmountPaid, 0) >  0              THEN 2
                            ELSE 1
                        END
                    WHERE RecordID IN ({recordIdsCsv})
                      AND PromisedDate < GETDATE()
                      AND (closed = 0 OR closed IS NULL)
                    """;
                await using var expCmd = new SqlCommand(expiredPtpSql, conn, tx);
                int expiredCount = await expCmd.ExecuteNonQueryAsync();

                if (expiredCount > 0)
                    _logger.LogInformation(
                        "SyncPayments: closed {Count} expired PTP(s) for ContractId {ContractId}.",
                        expiredCount, contractId);

                // 6e. Stamp LastPaymentsFetch
                const string stampSql = """
                    UPDATE ContractLmsConnections
                    SET LastPaymentsFetch = GETDATE(),
                        NextPaymentsFetch = DATEADD(MINUTE,
                            (SELECT DurationMinutes FROM RefreshCycles
                             WHERE  id = ContractLmsConnections.PaymentsRefreshCycle),
                            GETDATE())
                    WHERE ContractId = @contractId
                    """;
                await using var stampCmd = new SqlCommand(stampSql, conn, tx);
                stampCmd.Parameters.AddWithValue("@contractId", contractId);
                await stampCmd.ExecuteNonQueryAsync();

                tx.Commit();

                _logger.LogInformation(
                    "SyncPayments: ContractId {ContractId} — {CDCount} record(s) updated, {HistCount} history row(s) inserted, {CommCount} commission(s) calculated, {PtpCount} PTP(s) paid, {ExpCount} PTP(s) expired.",
                    contractId, cdTotals.Count, historyRows.Count, commissionsInserted, ptpTotals.Count, expiredCount);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                _logger.LogError(ex, "SyncPayments: transaction rolled back for ContractId {ContractId}.", contractId);
                throw;
            }
        }

        private static async Task StampPaymentsFetchAsync(SqlConnection conn, int contractId)
        {
            const string sql = """
                UPDATE ContractLmsConnections
                SET LastPaymentsFetch = GETDATE(),
                    NextPaymentsFetch = DATEADD(MINUTE,
                        (SELECT DurationMinutes FROM RefreshCycles
                         WHERE  id = ContractLmsConnections.PaymentsRefreshCycle),
                        GETDATE())
                WHERE ContractId = @contractId
                """;
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@contractId", contractId);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// For every active LMS contract that declares at least one filter bound
        /// (MinDays/MaxDays, MinAmount/MaxAmount, MinOlb/MaxOlb, MinArrears/MaxArrears):
        ///
        ///   1. Fetches its ContractData records whose current field values fall outside
        ///      those bounds, including previously-closed records that may now fit a
        ///      newly-added sibling contract.
        ///   2. Searches sibling contracts (same ConnectBox org + same company) for one
        ///      whose bounds match the record's current values and moves the record there.
        ///   3. If no sibling contract matches, marks the record as IsClosed = 1.
        ///   4. Writes a ContractMigrationLog row for every action taken.
        ///
        /// Agent assignment is always cleared on migration so the next allocation
        /// cycle re-distributes the record into the correct agent pool.
        ///
        /// The sibling pool is scoped by EntityId (ConnectBox org) + CompanyId so that
        /// records never migrate across organisations or across unrelated companies.
        /// </summary>
        public async Task<int> MigrateRecordsAsync(List<ContractLmsConnection> allContracts)
        {
            if (allContracts == null || allContracts.Count == 0) return 0;

            // Deduplicate by ContractId — keep the first occurrence (first category row).
            // This is safe because the migration only needs the filter bounds and CategoryId
            // for the initial assignment; allocation will re-distribute agents afterward.
            var uniqueContracts = allContracts
                .GroupBy(c => c.ContractId)
                .Select(g => g.First())
                .ToList();

            // Scope sibling pools by ConnectBox org (EntityId) AND company (CompanyId).
            // Using LmsEntityId (external ID) would risk cross-org record movement if two
            // tenants reference the same external LMS company.
            var siblingsByScope = uniqueContracts
                .GroupBy(c => $"{c.EntityId}:{c.CompanyId}")
                .ToDictionary(g => g.Key, g => g.ToList());

            string connStr = _config.GetConnectionString("DefaultConnection")!;
            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            int totalMigrated = 0;
            int totalClosed   = 0;
            int totalReopened = 0;

            foreach (var (scopeKey, siblings) in siblingsByScope)
            {
                // Only evaluate contracts that declare at least one filter bound.
                // Unconstrained contracts are catch-alls and can never produce a mismatch.
                var constrainedSources = siblings.Where(c =>
                    c.MinDays.HasValue    || c.MaxDays.HasValue    ||
                    c.MinAmount.HasValue  || c.MaxAmount.HasValue  ||
                    c.MinOlb.HasValue     || c.MaxOlb.HasValue     ||
                    c.MinArrears.HasValue || c.MaxArrears.HasValue).ToList();

                if (constrainedSources.Count == 0) continue;

                foreach (var source in constrainedSources)
                {
                    // Fetch all non-deleted records for this contract, including IsClosed=1
                    // records so we can re-evaluate them against newly added sibling contracts.
                    const string fetchSql = """
                        SELECT Id, LoanRefId, AmountDisbursed, DaysinArrears, ArrearsAmount, OLB,
                               ISNULL(IsClosed, 0) AS IsClosed
                        FROM   ContractData
                        WHERE  ContractId = @contractId
                          AND  (IsDeleted = 0 OR IsDeleted IS NULL)
                        """;

                    var records = new List<(int Id, string LoanRefId, decimal Amount, int Days, decimal Arrears, decimal Olb, bool WasClosed)>();

                    await using (var fetchCmd = new SqlCommand(fetchSql, conn))
                    {
                        fetchCmd.Parameters.AddWithValue("@contractId", source.ContractId);
                        await using var reader = await fetchCmd.ExecuteReaderAsync();
                        while (await reader.ReadAsync())
                        {
                            records.Add((
                                Id:        Convert.ToInt32(reader["Id"]),
                                LoanRefId: reader["LoanRefId"]      == DBNull.Value ? "" : reader["LoanRefId"].ToString()!,
                                Amount:    reader["AmountDisbursed"] == DBNull.Value ? 0m : Convert.ToDecimal(reader["AmountDisbursed"]),
                                Days:      reader["DaysinArrears"]   == DBNull.Value ? 0  : Convert.ToInt32(reader["DaysinArrears"]),
                                Arrears:   reader["ArrearsAmount"]   == DBNull.Value ? 0m : Convert.ToDecimal(reader["ArrearsAmount"]),
                                Olb:       reader["OLB"]             == DBNull.Value ? 0m : Convert.ToDecimal(reader["OLB"]),
                                WasClosed: Convert.ToInt32(reader["IsClosed"]) == 1
                            ));
                        }
                    }

                    if (records.Count == 0) continue;

                    // Candidates are:
                    //   a) Active records that no longer match the source contract's bounds.
                    //   b) Previously-closed records — re-check in case a new sibling now fits.
                    var candidates = records
                        .Where(r => r.WasClosed || !MatchesContract(r.Amount, r.Days, r.Arrears, r.Olb, source))
                        .ToList();

                    if (candidates.Count == 0) continue;

                    _logger.LogInformation(
                        "MigrateRecords: {Count} candidate(s) in ContractId {ContractId} (scope {Scope}).",
                        candidates.Count, source.ContractId, scopeKey);

                    using var tx = conn.BeginTransaction();
                    try
                    {
                        foreach (var record in candidates)
                        {
                            // Find the first sibling contract (different ContractId) whose bounds
                            // accept this record's current field values.
                            var target = siblings
                                .Where(c => c.ContractId != source.ContractId)
                                .FirstOrDefault(c => MatchesContract(record.Amount, record.Days, record.Arrears, record.Olb, c));

                            if (target != null)
                            {
                                int.TryParse(target.CategoryId, out int newCategoryId);

                                const string migrateSql = """
                                    UPDATE ContractData
                                    SET ContractId      = @newContractId,
                                        CategoryID      = @newCategoryId,
                                        AssignedAgent   = NULL,
                                        AssignedToAgent = 0,
                                        IsClosed        = 0,
                                        DateUpdated     = GETDATE()
                                    WHERE Id = @id
                                    """;

                                await using var upCmd = new SqlCommand(migrateSql, conn, tx);
                                upCmd.Parameters.AddWithValue("@newContractId", target.ContractId);
                                upCmd.Parameters.AddWithValue("@newCategoryId", newCategoryId > 0 ? (object)newCategoryId : DBNull.Value);
                                upCmd.Parameters.AddWithValue("@id", record.Id);
                                await upCmd.ExecuteNonQueryAsync();

                                var reason = record.WasClosed
                                    ? $"Re-opened — now matches ContractId {target.ContractId}"
                                    : $"Migrated — out of bounds for ContractId {source.ContractId}";

                                await WriteMigrationLogAsync(conn, tx,
                                    contractDataId: record.Id,
                                    loanRefId:      record.LoanRefId,
                                    fromContractId: source.ContractId,
                                    toContractId:   target.ContractId,
                                    amount:         record.Amount,
                                    days:           record.Days,
                                    arrears:        record.Arrears,
                                    olb:            record.Olb,
                                    reason:         reason);

                                if (record.WasClosed) totalReopened++;
                                else totalMigrated++;

                                _logger.LogInformation(
                                    "MigrateRecords: ContractDataId {Id} (Loan {Loan}) {Action} ContractId {From} → {To}.",
                                    record.Id, record.LoanRefId,
                                    record.WasClosed ? "re-opened and moved" : "moved",
                                    source.ContractId, target.ContractId);
                            }
                            else if (!record.WasClosed)
                            {
                                // No sibling matched — close the record.
                                // IsClosed keeps the record visible to operators; IsDeleted would hide it.
                                const string closeSql = """
                                    UPDATE ContractData
                                    SET IsClosed        = 1,
                                        AssignedAgent   = NULL,
                                        AssignedToAgent = 0,
                                        DateUpdated     = GETDATE()
                                    WHERE Id = @id
                                    """;

                                await using var closeCmd = new SqlCommand(closeSql, conn, tx);
                                closeCmd.Parameters.AddWithValue("@id", record.Id);
                                await closeCmd.ExecuteNonQueryAsync();

                                await WriteMigrationLogAsync(conn, tx,
                                    contractDataId: record.Id,
                                    loanRefId:      record.LoanRefId,
                                    fromContractId: source.ContractId,
                                    toContractId:   null,
                                    amount:         record.Amount,
                                    days:           record.Days,
                                    arrears:        record.Arrears,
                                    olb:            record.Olb,
                                    reason:         "Closed — no sibling contract matched current values");

                                totalClosed++;
                                _logger.LogWarning(
                                    "MigrateRecords: ContractDataId {Id} (Loan {Loan}) closed — " +
                                    "no match for Amount={Amount}, Days={Days}, Arrears={Arrears}, OLB={Olb}.",
                                    record.Id, record.LoanRefId, record.Amount, record.Days, record.Arrears, record.Olb);
                            }
                            // Previously-closed record with no new match → leave closed; no re-log.
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        _logger.LogError(ex, "MigrateRecords: transaction rolled back for ContractId {ContractId}.", source.ContractId);
                        throw;
                    }
                }
            }

            _logger.LogInformation(
                "MigrateRecords: complete — {Migrated} migrated, {Reopened} re-opened, {Closed} closed.",
                totalMigrated, totalReopened, totalClosed);

            return totalMigrated + totalReopened;
        }

        private static async Task WriteMigrationLogAsync(
            SqlConnection conn, SqlTransaction tx,
            int contractDataId, string loanRefId,
            int fromContractId, int? toContractId,
            decimal amount, int days, decimal arrears, decimal olb,
            string reason)
        {
            const string sql = """
                INSERT INTO ContractMigrationLog
                    (ContractDataId, LoanRefId, FromContractId, ToContractId,
                     AmountDisbursed, DaysinArrears, ArrearsAmount, OLB, Reason, MigratedDate)
                VALUES
                    (@contractDataId, @loanRefId, @fromContractId, @toContractId,
                     @amount, @days, @arrears, @olb, @reason, GETDATE())
                """;

            await using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@contractDataId",  contractDataId);
            cmd.Parameters.AddWithValue("@loanRefId",       string.IsNullOrEmpty(loanRefId) ? (object)DBNull.Value : loanRefId);
            cmd.Parameters.AddWithValue("@fromContractId",  fromContractId);
            cmd.Parameters.AddWithValue("@toContractId",    toContractId.HasValue ? (object)toContractId.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@amount",          amount);
            cmd.Parameters.AddWithValue("@days",            days);
            cmd.Parameters.AddWithValue("@arrears",         arrears);
            cmd.Parameters.AddWithValue("@olb",             olb);
            cmd.Parameters.AddWithValue("@reason",          reason);
            await cmd.ExecuteNonQueryAsync();
        }

        // Returns false as soon as the record's value violates any bound that is set.
        // A null bound means "no restriction on that dimension".
        private static bool MatchesContract(decimal amount, int days, decimal arrears, decimal olb, ContractLmsConnection c)
        {
            if (c.MinDays.HasValue      && days    < c.MinDays.Value)    return false;
            if (c.MaxDays.HasValue      && days    > c.MaxDays.Value)    return false;
            if (c.MinAmount.HasValue    && amount < c.MinAmount.Value) return false;
            if (c.MaxAmount.HasValue    && amount > c.MaxAmount.Value) return false;
            if (c.MinArrears.HasValue   && arrears < c.MinArrears.Value) return false;
            if (c.MaxArrears.HasValue   && arrears > c.MaxArrears.Value) return false;
            if (c.MinOlb.HasValue       && olb     < c.MinOlb.Value)     return false;
            if (c.MaxOlb.HasValue       && olb     > c.MaxOlb.Value)     return false;
            return true;
        }
    }
}