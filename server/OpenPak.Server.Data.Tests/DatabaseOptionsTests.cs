using System;
using System.Linq;
using OpenPak.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace OpenPak.Server.Data.Tests;

public class DatabaseOptionsTests
{
    [Fact]
    public void Unset_dsn_fails_validation_and_never_falls_back()
    {
        var o = new DatabaseOptions();
        o.ApplyEnvironment(_ => null);
        Assert.NotEmpty(o.Validate());
    }

    [Fact]
    public void Dsn_comes_from_the_environment()
    {
        var o = new DatabaseOptions();
        o.ApplyEnvironment(n => n == DatabaseOptions.EnvVar ? "postgres://u:p%40w@db:19432/photon" : null);
        Assert.Empty(o.Validate());
        var b = new NpgsqlConnectionStringBuilder(DatabaseOptions.Normalize(o.ConnectionString));
        Assert.Equal("db", b.Host); Assert.Equal(19432, b.Port); Assert.Equal("photon", b.Database);
        Assert.Equal("u", b.Username); Assert.Equal("p@w", b.Password); Assert.Equal(3, b.MaxPoolSize);
    }

    /// <summary>Restart test against a real Postgres: set PHOTON_TEST_DATABASE_URL (an empty scratch database). Skipped when unset.</summary>
    [Fact]
    public void State_written_by_one_instance_is_seen_by_the_next()
    {
        var url = Environment.GetEnvironmentVariable("PHOTON_TEST_DATABASE_URL");
        if (string.IsNullOrEmpty(url)) return;
        var opts = new DatabaseOptions { ConnectionString = url };
        var name = "r-" + Guid.NewGuid().ToString("N");
        using (var ctx = opts.CreateContext())
        {
            ctx.Realms.Add(new Realm { Name = name, DisplayName = "R" });
            ctx.SaveChanges();
            ctx.Database.ExecuteSqlRaw("INSERT INTO rewards_state (name, body) VALUES ({0}, 'x') ON CONFLICT (name) DO UPDATE SET body = 'x'", name);
        }
        using var reopened = opts.CreateContext();   // second boot: schema init is a no-op, data is there
        Assert.True(reopened.Realms.Any(r => r.Name == name));
        Assert.Equal("x", reopened.Database.SqlQueryRaw<string>("SELECT body AS \"Value\" FROM rewards_state WHERE name = {0}", name).AsEnumerable().First());
    }
}
