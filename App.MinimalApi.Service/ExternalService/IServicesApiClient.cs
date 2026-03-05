namespace App.MinimalApi.Service.ExternalService
{
    public interface IServicesApiClient
    {
        [Get("/v3.1/all?fields=name,capital,currencies")]
        Task<List<DTOCountryResponse>> GetAllCountriesAsync(CancellationToken token = default);
    }
}
