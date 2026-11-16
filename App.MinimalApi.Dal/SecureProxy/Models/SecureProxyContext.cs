namespace App.MinimalApi.Dal.SecureProxy.Models;

/// <summary>
/// DB-first mapping of the SecureProxyDb schema (Database/001_Initial.sql + 002_Extensions.sql).
/// Configuration tables are system-versioned temporal tables on SQL Server; their period columns
/// are HIDDEN, so the regular mapping below does not need to know about them.
/// </summary>
public partial class SecureProxyContext : DbContext
{
    public SecureProxyContext(DbContextOptions<SecureProxyContext> options) : base(options)
    {
    }

    public virtual DbSet<Destination> Destinations { get; set; }
    public virtual DbSet<Operation> Operations { get; set; }
    public virtual DbSet<ParameterRule> ParameterRules { get; set; }
    public virtual DbSet<HeaderRule> HeaderRules { get; set; }
    public virtual DbSet<EncryptionProfile> EncryptionProfiles { get; set; }
    public virtual DbSet<RequestLog> RequestLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Destination>(entity =>
        {
            entity.ToTable("Destinations");
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.BaseUrl).HasMaxLength(500);
            entity.Property(e => e.AllowedPorts).HasMaxLength(100);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<Operation>(entity =>
        {
            entity.ToTable("Operations");
            entity.HasIndex(e => e.OperationKey).IsUnique();
            entity.Property(e => e.OperationKey).HasMaxLength(100);
            entity.Property(e => e.HttpMethod).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.PathTemplate).HasMaxLength(300);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasOne(e => e.Destination).WithMany(d => d.Operations)
                .HasForeignKey(e => e.DestinationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.EncryptionProfile).WithMany()
                .HasForeignKey(e => e.EncryptionProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ParameterRule>(entity =>
        {
            entity.ToTable("ParameterRules");
            entity.HasIndex(e => new { e.OperationId, e.Name }).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.DataType).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.Location).HasMaxLength(10).IsUnicode(false);
            entity.Property(e => e.RegexPattern).HasMaxLength(200);
            entity.HasOne(e => e.Operation).WithMany(o => o.ParameterRules)
                .HasForeignKey(e => e.OperationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HeaderRule>(entity =>
        {
            entity.ToTable("HeaderRules");
            entity.HasIndex(e => new { e.OperationId, e.HeaderName }).IsUnique();
            entity.Property(e => e.HeaderName).HasMaxLength(100);
            entity.Property(e => e.RegexPattern).HasMaxLength(200);
            entity.HasOne(e => e.Operation).WithMany(o => o.HeaderRules)
                .HasForeignKey(e => e.OperationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EncryptionProfile>(entity =>
        {
            entity.ToTable("EncryptionProfiles");
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).HasMaxLength(100);
            entity.Property(e => e.Algorithm).HasMaxLength(20).IsUnicode(false);
            entity.Property(e => e.KeyReference).HasMaxLength(100);
            entity.Property(e => e.UpdatedBy).HasMaxLength(100);
            entity.Property(e => e.CreatedAt).HasPrecision(3);
            entity.Property(e => e.UpdatedAt).HasPrecision(3);
            entity.Property(e => e.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<RequestLog>(entity =>
        {
            entity.ToTable("RequestLogs");
            entity.HasIndex(e => e.CorrelationId);
            entity.HasIndex(e => e.StartedAt);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.OperationKey).HasMaxLength(100);
            entity.Property(e => e.StartedAt).HasPrecision(3);
            entity.Property(e => e.CompletedAt).HasPrecision(3);
            entity.Property(e => e.Outcome).HasMaxLength(30).IsUnicode(false);
            entity.Property(e => e.Reason).HasMaxLength(300);
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
