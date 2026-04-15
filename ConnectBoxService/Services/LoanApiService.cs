using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ConnectBoxService.Models;

namespace ConnectBoxService.Services
{
    public class LoanApiService : ILoanApiService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<LoanApiService> _logger;

        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public LoanApiService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<LoanApiService> logger)
        {
            // Bypass SSL for now — remove once correct cert is in place
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _httpClient = new HttpClient(handler);
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Calls POST /auth/token and returns the access token string.
        /// Now includes EntityId in the payload as required by the API.
        /// </summary>
        public async Task<string?> GetTokenAsync(string EntityId)
        {
            var baseUrl = _config["LoanApi:BaseUrl"]!;
            var tokenEndpoint = _config["LoanApi:TokenEndpoint"]!;
            var clientId = _config["LoanApi:ClientId"]!;
            var clientSecret = _config["LoanApi:ClientSecret"]!;
            var entityId = EntityId!;  // Added

            var payload = new { clientId, clientSecret, entityId };  // Added entityId
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _httpClient.PostAsync($"{baseUrl}{tokenEndpoint}", content);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Token request failed: {StatusCode} {Body}", response.StatusCode, body);
                    return null;
                }

                var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(body, _jsonOptions);
                return tokenResponse?.Data?.AccessToken;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception calling token endpoint.");
                return null;
            }
        }

        /// <summary>
        /// Calls POST /loans/GetLoansByFilter with the bearer token and returns the list of loans.
        /// No filters applied — fetch all loans for the entity in the token.
        /// </summary>
        public async Task<List<LoanDto>> GetLoansAsync(string token, ContractLmsConnection connection)
        {
            var baseUrl = _config["LoanApi:BaseUrl"]!;
            var loansEndpoint = _config["LoanApi:LoansEndpoint"]!;

            // No filters — fetch all loans for the entity embedded in the token
            var payload = new
            {
                minDays = connection.MinDays,
                maxDays = connection.MaxDays,
                minAmount = connection.MinAmount,
                maxAmount = connection.MaxAmount,
                minOlb = connection.MinOlb,
                maxOlb = connection.MaxOlb
            };

            var json = JsonSerializer.Serialize(payload);

            try
            {
                // Create a new HttpRequestMessage each time — cannot reuse the same instance
                var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}{loansEndpoint}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                ///_logger.LogInformation("Calling loans endpoint: {Url}", $"{baseUrl}{loansEndpoint}");

                var response = await _httpClient.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                ///_logger.LogInformation("Loans response: {StatusCode} | Body: {Body}", response.StatusCode, body);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Loans request failed: {StatusCode} {Body}", response.StatusCode, body);
                    return new List<LoanDto>();
                }

                var loanResponse = JsonSerializer.Deserialize<LoanApiResponse>(body, _jsonOptions);

                if (loanResponse?.Data?.Data == null)
                {
                    _logger.LogWarning("Loans response deserialized but Data is null. Raw body: {Body}", body);
                    return new List<LoanDto>();
                }

                _logger.LogInformation("Successfully fetched {Count} loans.", loanResponse.Data.Data.Count);
                return loanResponse.Data.Data;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception calling loans endpoint: {Url}", $"{baseUrl}{loansEndpoint}");
                return new List<LoanDto>();
            }
        }
    }
}