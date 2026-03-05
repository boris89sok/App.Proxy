namespace App.MinimalApi.Service.Extensions
{
    public class RawLoggingHandler(ILogger<RawLoggingHandler> logger) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            logger.LogInformation("External API Request URL: {Url}", request.RequestUri);

            var response = await base.SendAsync(request, cancellationToken);
            var rawJson = await response.Content.ReadAsStringAsync(cancellationToken);

            logger.LogInformation("External API Raw Response: {Json}", rawJson);

            return response;
        }
    }
}