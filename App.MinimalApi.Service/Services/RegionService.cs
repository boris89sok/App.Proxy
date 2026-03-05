namespace App.MinimalApi.Service.Services
{
    public interface IRegionService : IGenericDataService<DTORegion, Region>
    {
    }
    public class RegionService : GenericDataService<DTORegion, Region>, IRegionService
    {
        public RegionService(IMapper mapper, IRegionRepository repository) : base(mapper, repository)
        {
        }
    }
}
