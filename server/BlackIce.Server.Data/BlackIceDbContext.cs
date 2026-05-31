using Microsoft.EntityFrameworkCore;

namespace BlackIce.Server.Data;

public class BlackIceDbContext : DbContext
{
    public BlackIceDbContext(DbContextOptions<BlackIceDbContext> options) : base(options) { }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<ServerState> ServerState => Set<ServerState>();
    public DbSet<Realm> Realms => Set<Realm>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>().HasOne(a => a.Profile).WithOne()
            .HasForeignKey<Profile>(p => p.SteamId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Account>().Property(a => a.Level).HasConversion<int>();
    }
}
