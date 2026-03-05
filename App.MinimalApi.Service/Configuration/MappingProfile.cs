namespace App.MinimalApi.Service.Configuration
{
    public class MappingProfile : Profile
    {
        public MappingProfile()
        {
            CreateMap<DTORegion, Region>().ReverseMap();
        }
    }
}
