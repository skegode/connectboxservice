using ConnectBoxService;
using ConnectBoxService.Services;

var builder = Host.CreateApplicationBuilder(args);

// Register services
builder.Services.AddHttpClient();
builder.Services.AddScoped<IContractService, ContractService>();
builder.Services.AddScoped<ILoanApiService, LoanApiService>();
builder.Services.AddScoped<IContractDataService, ContractDataService>();

// Register the hosted worker
builder.Services.AddHostedService<LoanSyncWorker>();

var host = builder.Build();
host.Run();
