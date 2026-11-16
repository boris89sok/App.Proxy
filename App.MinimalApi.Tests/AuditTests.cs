namespace App.MinimalApi.Tests;

/// <summary>Required case 17 - one metadata-only audit row per execution attempt.</summary>
public sealed class AuditTests(ProxyWebAppFactory factory) : ProxyTestBase(factory), IClassFixture<ProxyWebAppFactory>
{
    [Fact]
    public async Task Successful_request_is_audited()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        var response = await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 777 } }, correlationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(correlationId, response.Headers.GetValues("X-Correlation-Id").Single());
        var row = await Factory.WaitForAuditAsync(correlationId);
        Assert.Equal("customer.get", row.OperationKey);
        Assert.Equal(TestData.CustomersDestinationId, row.DestinationId);
        Assert.True(row.Forwarded);
        Assert.Equal(200, row.UpstreamStatus);
        Assert.Equal(200, row.ProxyStatus);
        Assert.Equal("Success", row.Outcome);
        Assert.NotNull(row.CompletedAt);
        Assert.True(row.DurationMs >= 0);
        Assert.True(row.CompletedAt >= row.StartedAt);
    }

    [Fact]
    public async Task Rejected_request_is_audited_with_reason_and_without_forwarding()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "customer.delete-all", json = new { urlId = 5 } }, correlationId));

        var row = await Factory.WaitForAuditAsync(correlationId);
        Assert.Equal("customer.delete-all", row.OperationKey);
        Assert.False(row.Forwarded);
        Assert.Null(row.UpstreamStatus);
        Assert.Equal(403, row.ProxyStatus);
        Assert.Equal("Rejected", row.Outcome);
        Assert.Equal("OperationDisabled", row.Reason);
        AssertNotForwarded();
    }

    [Fact]
    public async Task Request_rejected_before_an_operation_is_identified_is_audited()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        await Client.SendAsync(ProxyWebAppFactory.ExecuteRaw("{\"clientId\":\"customer.get\",\"url\":\"https://evil\"}", correlationId: correlationId));

        var row = await Factory.WaitForAuditAsync(correlationId);
        Assert.Null(row.OperationKey);
        Assert.Equal(400, row.ProxyStatus);
        Assert.Equal("Rejected", row.Outcome);
        Assert.Equal("UnknownEnvelopeField:url", row.Reason);
    }

    [Fact]
    public async Task Upstream_failure_is_audited()
    {
        Upstream.Responder = (_, _) => Task.FromResult(RecordingHandler.Json(HttpStatusCode.InternalServerError, "boom"));
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } }, correlationId));

        var row = await Factory.WaitForAuditAsync(correlationId);
        Assert.True(row.Forwarded);
        Assert.Equal(500, row.UpstreamStatus);
        Assert.Equal("UpstreamError", row.Outcome);
    }

    [Fact]
    public async Task Audit_rows_contain_no_payload_values_or_credentials()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();
        await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "files.get", json = new { urlId = "../secret-salary-report" } }, correlationId));
        var rejected = await Factory.WaitForAuditAsync(correlationId);

        var everything = string.Join("|", Factory.WithDb(db => db.RequestLogs.AsNoTracking().ToList())
            .Select(r => $"{r.CorrelationId}{r.OperationKey}{r.Reason}{r.Outcome}"));

        Assert.Equal("PathTraversalAttempt", rejected.Reason);
        Assert.DoesNotContain("salary", everything);
        Assert.DoesNotContain(TestSecrets.ApiKey, everything);
        Assert.DoesNotContain("Bearer", everything);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_rejected_by_authentication_before_the_proxy_pipeline()
    {
        // Policy: authentication failures are recorded by the authentication/request logs, not RequestLogs,
        // because no operation execution was attempted (see DESIGN-NOTES "Audit policy").
        var anonymous = Factory.CreateClient();
        var correlationId = ProxyWebAppFactory.NewCorrelationId();

        var response = await anonymous.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } }, correlationId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await Task.Delay(300);
        Assert.False(Factory.WithDb(db => db.RequestLogs.Any(r => r.CorrelationId == correlationId)));
        AssertNotForwarded();
    }
}

/// <summary>Mandatory audit mode: an audit row must exist before anything is forwarded.</summary>
public sealed class MandatoryAuditFactory : ProxyWebAppFactory
{
    protected override IDictionary<string, string?> ExtraSettings => new Dictionary<string, string?>
    {
        ["Proxy:Audit:Mode"] = "Mandatory"
    };
}

public sealed class MandatoryAuditTests(MandatoryAuditFactory factory) : ProxyTestBase(factory), IClassFixture<MandatoryAuditFactory>
{
    [Fact]
    public async Task Audit_row_is_written_synchronously_and_completed()
    {
        var correlationId = ProxyWebAppFactory.NewCorrelationId();
        string? outcomeSeenByUpstream = null;
        Upstream.Responder = (_, _) =>
        {
            // When the upstream is called, the in-progress row must already be persisted.
            outcomeSeenByUpstream = Factory.WithDb(db => db.RequestLogs.AsNoTracking().Single(r => r.CorrelationId == correlationId).Outcome);
            return RecordingHandler.DefaultResponder(null!, default);
        };

        var response = await Client.SendAsync(ProxyWebAppFactory.Execute(new { clientId = "customer.get", json = new { urlId = 5 } }, correlationId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("InProgress", outcomeSeenByUpstream);
        var rows = Factory.WithDb(db => db.RequestLogs.AsNoTracking().Where(r => r.CorrelationId == correlationId).ToList());
        var row = Assert.Single(rows);
        Assert.Equal("Success", row.Outcome);
    }
}
