namespace App.MinimalApi.Service.Extensions
{
    internal static class HttpClientExtension
    {
        public static IServiceCollection AddHttpClientExtension(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddTransient<RawLoggingHandler>();
            services
                .AddRefitClient<IServicesApiClient>()
                .ConfigureHttpClient((sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptionsMonitor<EnvOptions>>();
                    if (string.IsNullOrEmpty(options.CurrentValue.ApiUrl))
                    {
                        throw new InvalidOperationException("ApiUrl is not configured in EnvOptions.");
                    }

                    client.BaseAddress = new Uri(options.CurrentValue.ApiUrl);
                })
                .AddHttpMessageHandler<RawLoggingHandler>()
                .ConfigurePrimaryHttpMessageHandler(() =>
                {
                    var handler = new SocketsHttpHandler
                    {
                        EnableMultipleHttp2Connections = false,
                        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
                    };

                    handler.SslOptions.RemoteCertificateValidationCallback =
                        (sender, cert, chain, errors) => true;

                    return handler;
                });

            return services;
        }
    }
}