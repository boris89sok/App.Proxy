namespace App.MinimalApi.Service
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
        {
            // One options object for the whole application (the Proxy section).
            services.AddValidatedOptions<EnvOptions>(configuration.GetSection(EnvOptions.ProxySectionName));

            services.AddSecureProxy(configuration);

            return services;
        }
    }
}
