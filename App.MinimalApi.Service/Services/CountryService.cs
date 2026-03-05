namespace App.MinimalApi.Service.Services
{
    public interface ICountryService
    {
        Task<List<DTOCountryResponse>> GetAllAsync();
    }

    public sealed class CountryService(
        IServicesApiClient servicesApiClient,
        ILogger<CountryService> logger,
        IOptions<EnvOptions> envOptions) : ICountryService
    {
        private readonly IServicesApiClient _servicesApiClient = servicesApiClient;
        private readonly ILogger<CountryService> _logger = logger;
        private readonly IOptions<EnvOptions> _envOptions = envOptions;

        public async Task<List<DTOCountryResponse>> GetAllAsync()
        {
            {
                try
                {
                    _logger.LogInformation("CountryService GetAllAsync start");

                    var countries = await _servicesApiClient.GetAllCountriesAsync();
                    return countries;
                }
                catch (Exception ex)
                {
                    _logger.LogError($"CountryService GetAllAsync error Exception: {ex.Message} InnerException: {ex.InnerException?.Message}");
                    return null;
                }
            }
        }
    }
}
