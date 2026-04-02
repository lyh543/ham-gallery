using FluentGallery.Models;
using Microsoft.EntityFrameworkCore;

namespace FluentGallery.Data;

/// <summary>
/// EF Core DbContext for Fluent Gallery.
/// Configure once; create per-operation via <see cref="IDbContextFactory{GalleryDbContext}"/>.
/// </summary>
public sealed class GalleryDbContext : DbContext
{
    public DbSet<Album>     Albums     => Set<Album>();
    public DbSet<Photo>     Photos     => Set<Photo>();
    public DbSet<Thumbnail> Thumbnails => Set<Thumbnail>();
    public DbSet<Setting>   Settings   => Set<Setting>();

    public GalleryDbContext(DbContextOptions<GalleryDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── Albums ───────────────────────────────────────────────────────────
        mb.Entity<Album>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).IsRequired();
            e.Property(a => a.IsPinned).HasDefaultValue(false);
            e.Property(a => a.SortOrder).HasDefaultValue(0);

            // Transient column — not stored in DB
            e.Ignore(a => a.PhotoCount);

            e.HasIndex(a => new { a.IsPinned, a.SortOrder })
             .HasDatabaseName("idx_albums_pinned");
        });

        // ── Photos ───────────────────────────────────────────────────────────
        mb.Entity<Photo>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.FilePath).IsRequired();
            e.Property(p => p.FileName).IsRequired();
            e.Property(p => p.FileSize).IsRequired();
            e.Property(p => p.IsPinned).HasDefaultValue(false);

            // Unique constraint on FilePath
            e.HasIndex(p => p.FilePath)
             .IsUnique()
             .HasDatabaseName("idx_photos_filepath");

            // Indices from PROMPT.md §4
            e.HasIndex(p => p.AlbumId)
             .HasDatabaseName("idx_photos_album");
            e.HasIndex(p => p.TakenAt)
             .HasDatabaseName("idx_photos_takenAt");
            e.HasIndex(p => p.ModifiedAt)
             .HasDatabaseName("idx_photos_modifiedAt");

            // FK → Albums: ON DELETE SET NULL (nullable FK)
            e.HasOne<Album>()
             .WithMany()
             .HasForeignKey(p => p.AlbumId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Thumbnails ───────────────────────────────────────────────────────
        mb.Entity<Thumbnail>(e =>
        {
            // PhotoId is both PK and FK (1:1 with Photo)
            e.HasKey(t => t.PhotoId);

            e.HasOne<Photo>()
             .WithOne()
             .HasForeignKey<Thumbnail>(t => t.PhotoId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Settings ─────────────────────────────────────────────────────────
        mb.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
        });
    }
}
