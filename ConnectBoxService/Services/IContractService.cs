using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public interface IContractService
    {
        Task<List<ContractLmsConnection>> GetContractConnectionsAsync();
    }
}
