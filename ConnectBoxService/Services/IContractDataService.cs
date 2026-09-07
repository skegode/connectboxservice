using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public interface IContractDataService
    {
        /// <summary>
        /// Upserts loans for a contract. Matches on LoanId + Entityid across ALL contracts so
        /// records that have moved between contracts are updated in place rather than duplicated.
        /// Returns the set of integer category IDs that received new or migrated records;
        /// the caller should run AllocateContractsToAgentsAsync for each.
        /// </summary>
        Task<HashSet<int>> UpsertLoansAsync(int contractId, string entityId, string categoryId, List<LoanDto> loans);

        Task AllocateContractsToAgentsAsync(int categoryId, int orgId);

        Task SyncPaymentsAsync(int contractId, string EntityId, List<LmsPaymentDto> payments, decimal commissionRate);

        Task<int> MigrateRecordsAsync(List<ContractLmsConnection> allContracts);
    }
}