namespace App.MinimalApi.Dal.NORTHWND.Models;

public partial class NorthwndContext : DbContext
{
    public NorthwndContext(DbContextOptions<NorthwndContext> options)
        : base(options)
    {
    }

    public virtual DbSet<Region> Regions { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Region>(entity =>
        {
            entity.HasKey(e => e.RegionId).IsClustered(false);

            entity.ToTable("Region");

            entity.Property(e => e.RegionId)
                .ValueGeneratedNever()
                .HasColumnName("RegionID");
            entity.Property(e => e.RegionDescription)
                .HasMaxLength(50)
                .IsFixedLength();
        });

        OnModelCreatingPartial(modelBuilder);
    }

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
