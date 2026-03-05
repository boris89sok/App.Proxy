using Microsoft.EntityFrameworkCore;

namespace App.MinimalApi.Service
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
        {
            var env = configuration["AppSettings:Env"] ?? throw new ArgumentNullException("AppSettings:Env not found in configuration");

            services.AddDbContext<NorthwndContext>(options => options.UseSqlServer(configuration.GetConnectionString("DefaultConnection")));
            services.AddOptions<EnvOptions>()
                .Bind(configuration.GetSection($"AppSettings:{env}"))
                .ValidateDataAnnotations()
                .ValidateOnStart();

            services.AddAutoMapper(AppDomain.CurrentDomain.GetAssemblies());
            services.AddHttpClientExtension(configuration);
            services.AddScoped<ICountryService, CountryService>();
            services.AddScoped<IRegionRepository, RegionRepository>();
            services.AddScoped<IRegionService, RegionService>();

            return services;
        }
    }
}