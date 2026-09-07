using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public interface ILoanApiService
    {
        Task<string?>               GetTokenAsync(string EntityId);
        Task<List<LoanDto>>         GetLoansAsync(string token, ContractLmsConnection connection);
        Task<List<LmsPaymentDto>>   GetPaymentsAsync(string token, DateTime dateFrom, DateTime dateTo);
    }
}
