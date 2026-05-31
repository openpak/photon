using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BlackIce.Server.Data;

/// <summary>
/// Design-time factory used by the EF Core tools (<c>dotnet ef migrations add</c> /
/// <c>dotnet ef database update</c>). It builds the context against SQLite, which is the default
/// provider and the one the committed migrations target. It is never used at runtime — the running
/// server builds its context through <see cref="DatabaseOptions.CreateContext"/>.
/// </summary>
public sealed class BlackIceDbContextDesignFactory : IDesignTimeDbContextFactory<BlackIceDbContext>
{
    public BlackIceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BlackIceDbContext>()
            .UseSqlite("Data Source=blackice-design.db")
            .Options;
        return new BlackIceDbContext(options);
    }
}
