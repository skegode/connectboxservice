using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public interface IContractDataService
    {
        // Method 1: Existing Upsert logic
        Task UpsertLoansAsync(int contractId, string entityId, string categoryId, List<LoanDto> loans);

        // Method 2: ADD THIS LINE so the Worker can call the allocation logic
        Task AllocateContractsToAgentsAsync(int categoryId, int orgId);
    }
}