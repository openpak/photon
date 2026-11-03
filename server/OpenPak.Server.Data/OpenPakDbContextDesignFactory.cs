using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenPak.Server.Data;

/// <summary>Design-time factory for the EF Core tools. Never used at runtime.</summary>
public sealed class OpenPakDbContextDesignFactory : IDesignTimeDbContextFactory<OpenPakDbContext>
{
    public OpenPakDbContext CreateDbContext(string[] args)
    {
        var dsn = Environment.GetEnvironmentVariable(DatabaseOptions.EnvVar) ?? "Host=localhost;Database=photon_design";
        return new OpenPakDbContext(new DbContextOptionsBuilder<OpenPakDbContext>().UseNpgsql(DatabaseOptions.Normalize(dsn)).Options);
    }
}
