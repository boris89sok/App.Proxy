namespace App.MinimalApi.Dal.NORTHWND.Repositories
{
    public interface IRegionRepository : IGenericRepository<Region>
    {
    }
    public class RegionRepository : GenericRepository<Region, NorthwndContext>, IRegionRepository
    {
        public RegionRepository(NorthwndContext context) : base(context) { }
    }
}
