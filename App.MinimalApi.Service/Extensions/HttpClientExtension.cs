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
                .ConfigurePrimaryHttpMessageHandler(CreatePrimaryHandler);

            return services;
        }

        /// <summary>
        /// DEFECT FIX: the previous handler set RemoteCertificateValidationCallback = (...) => true,
        /// which disabled TLS certificate validation (any MITM certificate was accepted).
        /// The default validation is now used.
        /// </summary>
        internal static SocketsHttpHandler CreatePrimaryHandler() => new()
        {
            EnableMultipleHttp2Connections = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
    }
}
