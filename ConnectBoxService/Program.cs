using ConnectBoxService;
using ConnectBoxService.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = Host.CreateApplicationBuilder(args);

// Register services
builder.Services.AddHttpClient();
builder.Services.AddScoped<IContractService, ContractService>();
builder.Services.AddScoped<ILoanApiService, LoanApiService>();
builder.Services.AddScoped<IContractDataService, ContractDataService>();

// Register the hosted worker
builder.Services.AddHostedService<LoanSyncWorker>();

if (WindowsServiceHelpers.IsWindowsService())
{
    Directory.SetCurrentDirectory(AppContext.BaseDirectory);
}

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "Connect Box Service";
});

var host = builder.Build();
host.Run();
