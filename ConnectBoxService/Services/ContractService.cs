using Microsoft.Data.SqlClient;
using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public class ContractService : IContractService
    {
        private readonly IConfiguration _config;
        private readonly ILogger<ContractService> _logger;

        public ContractService(IConfiguration config, ILogger<ContractService> logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Fetches all active contract LMS connections with their refresh cycle settings.
        /// </summary>
        public async Task<List<ContractLmsConnection>> GetContractConnectionsAsync()
        {
            var connections = new List<ContractLmsConnection>();
            string connStr  = _config.GetConnectionString("DefaultConnection")!;

            await using var conn = new SqlConnection(connStr);
            await conn.OpenAsync();

            const string sql = """"
                SELECT
                    l.Id,
                    l.ContractId,           
                    l.DataRefreshCycle,     
                    l.PaymentsRefreshCycle, 
                	G.ID AS CategoryId,
                    l.MinDays,
                    l.MaxDays,
                    l.MinAmount,
                    l.MaxAmount,
                    l.MinOlb,
                    l.MaxOlb,
                    c.EntityId,
                    o.LmsEntityId,
                    d.Name            AS DataRefreshCycleName,
                    d.DurationMinutes AS DataRefreshCycleMinutes,
                    p.Name            AS PaymentsRefreshCycleName,
                    p.DurationMinutes AS PaymentsRefreshCycleMinutes
                FROM ContractLmsConnections l
                INNER JOIN contracts      c ON c.id        = l.ContractId
                INNER JOIN Companies      o ON o.ID        = c.CompanyID
                INNER JOIN RefreshCycles d ON d.id        = l.DataRefreshCycle
                INNER JOIN RefreshCycles p ON p.id        = l.PaymentsRefreshCycle 
                INNER JOIN Categories G ON G.ContractyID=C.ID
                WHERE c.DataSource = 2
                """";

            await using var cmd    = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                connections.Add(new ContractLmsConnection
                {
                    Id                         = reader["Id"]                         == DBNull.Value ? 0  : Convert.ToInt32(reader["Id"]),
                    ContractId                 = reader["ContractId"]                 == DBNull.Value ? 0  : Convert.ToInt32(reader["ContractId"]),
                    CategoryId = reader["CategoryId"] == DBNull.Value ? "" : reader["CategoryId"].ToString()!,
                    DataRefreshCycle           = reader["DataRefreshCycle"]           == DBNull.Value ? 0  : Convert.ToInt32(reader["DataRefreshCycle"]),
                    PaymentsRefreshCycle       = reader["PaymentsRefreshCycle"]       == DBNull.Value ? 0  : Convert.ToInt32(reader["PaymentsRefreshCycle"]),
                    EntityId = reader["Entityid"] == DBNull.Value ? "" : reader["Entityid"].ToString()!,
                    LmsEntityId                = reader["LmsEntityId"]               == DBNull.Value ? "" : reader["LmsEntityId"].ToString()!,
                    DataRefreshCycleName       = reader["DataRefreshCycleName"]       == DBNull.Value ? "" : reader["DataRefreshCycleName"].ToString()!,
                    DataRefreshCycleMinutes    = reader["DataRefreshCycleMinutes"]    == DBNull.Value ? 60 : Convert.ToInt32(reader["DataRefreshCycleMinutes"]),
                    PaymentsRefreshCycleName   = reader["PaymentsRefreshCycleName"]   == DBNull.Value ? "" : reader["PaymentsRefreshCycleName"].ToString()!,
                    PaymentsRefreshCycleMinutes = reader["PaymentsRefreshCycleMinutes"] == DBNull.Value ? 60 : Convert.ToInt32(reader["PaymentsRefreshCycleMinutes"]),
                    MinDays = reader["MinDays"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MinDays"]),
                    MaxDays = reader["MaxDays"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MaxDays"]),
                    MinAmount = reader["MinAmount"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["MinAmount"]),
                    MaxAmount = reader["MaxAmount"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["MaxAmount"]),
                    MinOlb = reader["MinOlb"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["MinOlb"]),
                    MaxOlb = reader["MaxOlb"] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(reader["MaxOlb"]),



                });
            }

            _logger.LogInformation("Loaded {Count} contract connections.", connections.Count);
            return connections;
        }
    }
}
