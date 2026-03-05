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
            .Produces<BaseResponseDto<List<DTOCountryResponse>>>(StatusCodes.Status200OK)
            .Produces<BaseResponseDto>(StatusCodes.Status400BadRequest)
            .Produces<BaseResponseDto>(StatusCodes.Status500InternalServerError)
            .WithName("GetCountries")
            .WithTags("Countries");

            group.MapGet("/Regions", async ([FromServices] IRegionService regionService) =>
            {
                var response = await regionService.GetAllAsync();
                return Results.Ok(BaseResponseDto.CreateSuccessResponse(response));
            })
            .Produces<BaseResponseDto<List<DTOCountryResponse>>>(StatusCodes.Status200OK)
            .Produces<BaseResponseDto>(StatusCodes.Status400BadRequest)
            .Produces<BaseResponseDto>(StatusCodes.Status500InternalServerError)
            .WithName("GetRegions")
            .WithTags("Regions");
        }
    }
}
