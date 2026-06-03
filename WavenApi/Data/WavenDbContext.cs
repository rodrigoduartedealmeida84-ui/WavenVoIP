using Microsoft.EntityFrameworkCore;
using WavenApi.Models;

namespace WavenApi.Data;

public class WavenDbContext(DbContextOptions<WavenDbContext> options) : DbContext(options)
{
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Favorito> Favoritos => Set<Favorito>();
    public DbSet<CompanyConfigEntry> CompanyConfig => Set<CompanyConfigEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.NumeroNormalizado);
            e.HasIndex(c => c.AtualizadoEm);
            e.Property(c => c.Id).IsRequired();
            e.Property(c => c.Nome).IsRequired();
            e.Property(c => c.Numero).IsRequired();
            e.Property(c => c.NumeroNormalizado).IsRequired();
        });

        modelBuilder.Entity<Favorito>(e =>
        {
            e.HasKey(f => f.Id);
            // Garante que não duplica: mesmo contato + mesmo ramal + mesmo tipo
            e.HasIndex(f => new { f.ContactId, f.Ramal, f.TipoFavorito }).IsUnique();
            e.HasOne(f => f.Contact)
             .WithMany(c => c.Favoritos)
             .HasForeignKey(f => f.ContactId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CompanyConfigEntry>(e =>
        {
            e.HasKey(c => c.Id);
        });
    }
}
