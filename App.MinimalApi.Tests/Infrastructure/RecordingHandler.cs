namespace App.MinimalApi.Tests.Infrastructure;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string[]> Headers, bool HadBody);

/// <summary>
/// Mock destination: the primary handler of the proxy's upstream HttpClient.
/// Records EVERY request that would have reached the network, so tests can prove that rejected
/// requests never reached the destination (a status code alone is not sufficient proof).
/// </summary>
public sealed class RecordingHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public IReadOnlyList<RecordedRequest> Requests => _requests.ToArray();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } = DefaultResponder;

    /// <summary>Completed when the cancellation token of an in-flight upstream call fires.</summary>
    public TaskCompletionSource UpstreamCancellationObserved { get; private set; } = NewTcs();

    public void Reset()
    {
        _requests.Clear();
        Responder = DefaultResponder;
        UpstreamCancellationObserved = NewTcs();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
        _requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, headers, request.Content is not null));

        using var registration = cancellationToken.Register(() => UpstreamCancellationObserved.TrySetResult());
        return await Responder(request, cancellationToken);
    }

    public static Task<HttpResponseMessage> DefaultResponder(HttpRequestMessage request, CancellationToken _) =>
        Task.FromResult(Json(HttpStatusCode.OK, "{\"id\":5,\"name\":\"Ada\"}"));

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "upstream-session=secret");
        response.Headers.TryAddWithoutValidation("Server", "internal-customers-01.corp.local");
        return response;
    }

    private static TaskCompletionSource NewTcs() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
