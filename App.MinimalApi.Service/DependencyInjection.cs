using App.MinimalApi.Service.Proxy;

namespace App.MinimalApi.Service
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
        {
            var env = configuration["AppSettings:Env"] ?? throw new ArgumentNullException("AppSettings:Env not found in configuration");

            // One options object for the whole application: environment values + proxy settings.
            services.AddValidatedOptions<EnvOptions>(configuration.GetSection($"AppSettings:{env}"))
                .Bind(configuration.GetSection(EnvOptions.ProxySectionName));

            services.AddHttpClientExtension(configuration);

            services.AddSecureProxy(configuration);

            return services;
        }
    }
}
