namespace App.MinimalApi.Webapi.Endpoints
{
    public class MinimalEndpoints : ICarterModule
    {
        public void AddRoutes(IEndpointRouteBuilder app)
        {
            var group = app.MapGroup("/Minimal/api/v1");

            group.MapGet("/countries", async ([FromServices] ICountryService countryService) =>
            {
                var response = await countryService.GetAllAsync();
                return Results.Ok(BaseResponseDto.CreateSuccessResponse(response));
            })
            .WithName("GetCountries")
            .WithTags("Countries");
        }
    }
}
