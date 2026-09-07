using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenPak.Server.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations add</c> /
/// <c>dotnet ef database update</c>). It builds the context against SQLite, which is the default
/// provider and the one the committed migrations target. It is never used at runtime — the running
/// server builds its context through <see cref="DatabaseOptions.CreateContext"/>.
/// </summary>
public sealed class OpenPakDbContextDesignFactory : IDesignTimeDbContextFactory<OpenPakDbContext>
{
    public OpenPakDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpenPakDbContext>()
            .UseSqlite("Data Source=openpak-design.db")
            .Options;
        return new OpenPakDbContext(options);
    }
}
