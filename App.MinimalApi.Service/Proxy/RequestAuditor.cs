namespace App.MinimalApi.Service.Proxy;

/// <summary>Mutable audit record collected during one execution. Metadata only.</summary>
public sealed class AuditRecord
{
    public string CorrelationId { get; set; } = string.Empty;
    public string? OperationKey { get; set; }
    public int? DestinationId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int? DurationMs { get; set; }
    public bool Forwarded { get; set; }
    public int? UpstreamStatus { get; set; }
    public int ProxyStatus { get; set; }
    public string Outcome { get; set; } = AuditOutcome.Failed;
    public string? Reason { get; set; }

    /// <summary>Row id when the record was persisted before forwarding (Mandatory mode).</summary>
    public long? PersistedId { get; set; }

    public RequestLog ToEntity() => new()
    {
        Id = PersistedId ?? 0,
        CorrelationId = CorrelationId.Truncate(64)!,
        OperationKey = OperationKey.Truncate(100),
        DestinationId = DestinationId,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        DurationMs = DurationMs,
        Forwarded = Forwarded,
        UpstreamStatus = UpstreamStatus,
        ProxyStatus = ProxyStatus,
        Outcome = Outcome.Truncate(30)!,
        Reason = Reason.Truncate(300)
    };

}

/// <summary>
/// Audit policy (see DESIGN-NOTES.md):
/// every execution attempt - successful, rejected or failed - produces exactly one RequestLogs row.
/// </summary>
public interface IRequestAuditor
{
    /// <summary>
    /// Called immediately before forwarding. In Mandatory mode the record is persisted synchronously and
    /// <c>false</c> is returned if that is not possible - the request must then NOT be forwarded.
    /// </summary>
    Task<bool> TryBeginForwardAsync(AuditRecord record, CancellationToken cancellationToken);

    /// <summary>Records the final outcome. Never throws.</summary>
    Task CompleteAsync(AuditRecord record);
}

/// <summary>Bounded in-memory queue between request threads and the audit writer.</summary>
public sealed class AuditChannel
{
    public AuditChannel(IOptions<EnvOptions> options)
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<RequestLog>(new BoundedChannelOptions(options.Value.Audit.ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full -> logged, never blocks requests
            SingleReader = true,
            SingleWriter = false
        });
    }

    public Channel<RequestLog> Channel { get; }
}

public sealed class RequestAuditor(
    AuditChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<EnvOptions> options,
    ILogger<RequestAuditor> logger) : IRequestAuditor
{
    private bool Mandatory => options.Value.Audit.Mode == AuditMode.Mandatory;

    public async Task<bool> TryBeginForwardAsync(AuditRecord record, CancellationToken cancellationToken)
    {
        if (!Mandatory)
        {
            return true;
        }

        try
        {
            var entity = record.ToEntity();
            entity.Outcome = "InProgress";
            entity.ProxyStatus = 0;
            entity.Forwarded = true;
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IRequestLogRepository>().InsertAsync(entity, cancellationToken);
            record.PersistedId = entity.Id;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Mandatory audit write failed; request {CorrelationId} will not be forwarded", record.CorrelationId);
            return false;
        }
    }

    public async Task CompleteAsync(AuditRecord record)
    {
        var entity = record.ToEntity();
        try
        {
            if (Mandatory)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<IRequestLogRepository>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (record.PersistedId is null)
                {
                    await repository.InsertAsync(entity, cts.Token);
                }
                else
                {
                    await repository.UpdateAsync(entity, cts.Token);
                }
                return;
            }

            if (!channel.Channel.Writer.TryWrite(entity))
            {
                LogFallback(logger, entity, "AuditQueueFull");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed for {CorrelationId}", record.CorrelationId);
            LogFallback(logger, entity, "AuditWriteFailed");
        }
    }

    /// <summary>Last-resort audit trail: the structured log sink receives the same (non-sensitive) metadata.</summary>
    internal static void LogFallback(ILogger logger, RequestLog e, string why) =>
        logger.LogWarning(
            "AuditFallback {Why} {CorrelationId} {OperationKey} {DestinationId} {StartedAt} {DurationMs} {Forwarded} {UpstreamStatus} {ProxyStatus} {Outcome} {Reason}",
            why, e.CorrelationId, e.OperationKey, e.DestinationId, e.StartedAt, e.DurationMs, e.Forwarded,
            e.UpstreamStatus, e.ProxyStatus, e.Outcome, e.Reason);
}

/// <summary>Drains the audit channel in batches; retries with backoff while SQL Server is unavailable.</summary>
public sealed class AuditWriterService(
    AuditChannel channel,
    IServiceScopeFactory scopeFactory,
    IOptions<EnvOptions> options,
    ILogger<AuditWriterService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = channel.Channel.Reader;
        var batchSize = options.Value.Audit.BatchSize;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                var batch = new List<RequestLog>(batchSize);
                while (batch.Count < batchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }
                await PersistAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down - flush what is left below
        }

        var remaining = new List<RequestLog>();
        while (reader.TryRead(out var item)) remaining.Add(item);
        if (remaining.Count > 0)
        {
            using var flushCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await PersistAsync(remaining, flushCts.Token);
        }
    }

    private async Task PersistAsync(List<RequestLog> batch, CancellationToken cancellationToken)
    {
        var attempts = Math.Max(1, options.Value.Audit.MaxWriteAttempts);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                foreach (var entity in batch) entity.Id = 0; // a failed attempt must not leak key values into the retry
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IRequestLogRepository>().AddBatchAsync(batch, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit batch write failed (attempt {Attempt}/{Attempts}, {Count} records)", attempt, attempts, batch.Count);
                if (attempt < attempts)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        foreach (var entity in batch)
        {
            RequestAuditor.LogFallback(logger, entity, "AuditPersistFailed");
        }
    }
}
