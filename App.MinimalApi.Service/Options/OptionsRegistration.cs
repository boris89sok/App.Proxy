namespace App.MinimalApi.Service.Options
{
    public static class OptionsRegistration
    {
        /// <summary>
        /// The one options registration pattern of the solution: bind, validate the data annotations,
        /// and fail at startup (fail closed) instead of at first use.
        /// </summary>
        public static OptionsBuilder<T> AddValidatedOptions<T>(this IServiceCollection services, IConfiguration section)
            where T : class =>
            services.AddOptions<T>()
                .Bind(section)
                .ValidateDataAnnotations()
                .ValidateOnStart();
    }
}
