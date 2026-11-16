namespace App.MinimalApi.Dal.SecureProxy.Repositories;

/// <summary>Configuration rows updated with optimistic concurrency (SQL Server rowversion).</summary>
public interface IConfigRepository<TEntity> : IGenericRepository<TEntity> where TEntity : class
{
    /// <summary>Sets the rowversion the caller last read, so SaveChanges fails with DbUpdateConcurrencyException when stale.</summary>
    void SetExpectedRowVersion(TEntity entity, byte[] rowVersion);

    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

public abstract class ConfigRepository<TEntity>(SecureProxyContext context)
    : GenericRepository<TEntity, SecureProxyContext>(context), IConfigRepository<TEntity> where TEntity : class
{
    public void SetExpectedRowVersion(TEntity entity, byte[] rowVersion) =>
        Context.Entry(entity).Property("RowVersion").OriginalValue = rowVersion;

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => Context.SaveChangesAsync(cancellationToken);
}

public interface IOperationRepository : IConfigRepository<Operation>
{
    /// <summary>Loads an operation with its full trusted configuration graph (read-only, untracked).</summary>
    Task<Operation?> GetByKeyWithGraphAsync(string operationKey, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetKeysByDestinationAsync(int destinationId, CancellationToken cancellationToken = default);
}

public class OperationRepository(SecureProxyContext context) : ConfigRepository<Operation>(context), IOperationRepository
{
    public Task<Operation?> GetByKeyWithGraphAsync(string operationKey, CancellationToken cancellationToken = default) =>
        Context.Operations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(o => o.Destination)
            .Include(o => o.EncryptionProfile)
            .Include(o => o.ParameterRules)
            .Include(o => o.HeaderRules)
            .FirstOrDefaultAsync(o => o.OperationKey == operationKey, cancellationToken);

    public async Task<IReadOnlyList<string>> GetKeysByDestinationAsync(int destinationId, CancellationToken cancellationToken = default) =>
        await Context.Operations.AsNoTracking()
            .Where(o => o.DestinationId == destinationId)
            .Select(o => o.OperationKey)
            .ToListAsync(cancellationToken);
}

public interface IDestinationRepository : IConfigRepository<Destination>;

public class DestinationRepository(SecureProxyContext context) : ConfigRepository<Destination>(context), IDestinationRepository;

/// <summary>Write-only access to the audit table. Audit rows are append-mostly and never read by the request path.</summary>
public interface IRequestLogRepository
{
    Task AddBatchAsync(IReadOnlyCollection<RequestLog> logs, CancellationToken cancellationToken = default);

    Task InsertAsync(RequestLog log, CancellationToken cancellationToken = default);

    Task UpdateAsync(RequestLog log, CancellationToken cancellationToken = default);
}

public class RequestLogRepository : IRequestLogRepository
{
    private readonly SecureProxyContext _context;

    public RequestLogRepository(SecureProxyContext context)
    {
        _context = context;
    }

    public async Task AddBatchAsync(IReadOnlyCollection<RequestLog> logs, CancellationToken cancellationToken = default)
    {
        _context.RequestLogs.AddRange(logs);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            foreach (var log in logs)
            {
                _context.Entry(log).State = EntityState.Detached;
            }
        }
    }

    public async Task InsertAsync(RequestLog log, CancellationToken cancellationToken = default)
    {
        _context.RequestLogs.Add(log);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _context.Entry(log).State = EntityState.Detached;
        }
    }

    public async Task UpdateAsync(RequestLog log, CancellationToken cancellationToken = default)
    {
        _context.RequestLogs.Update(log);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _context.Entry(log).State = EntityState.Detached;
        }
    }
}
