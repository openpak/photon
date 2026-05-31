using System.IO;
using BlackIce.Server.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BlackIce.Server.Data.Tests;

/// <summary>
/// SQLite Data Source anchoring: a relative db file must resolve next to the EXE (AppContext.
/// BaseDirectory), not the launch working directory — otherwise the server reads/writes a
/// different blackice.db depending on where it was started from.
/// </summary>
public class DatabaseOptionsTests
{
    [Fact]
    public void Relative_data_source_is_anchored_to_base_directory()
    {
        var anchored = DatabaseOptions.AnchorSqliteFile("Data Source=blackice.db");
        var src = new SqliteConnectionStringBuilder(anchored).DataSource;
        Assert.True(Path.IsPathRooted(src));
        Assert.StartsWith(AppContext.BaseDirectory, src);
        Assert.EndsWith("blackice.db", src);
    }

    [Fact]
    public void In_memory_data_source_is_untouched()
    {
        const string cs = "Data Source=:memory:";
        Assert.Equal(":memory:", new SqliteConnectionStringBuilder(DatabaseOptions.AnchorSqliteFile(cs)).DataSource);
    }

    [Fact]
    public void Absolute_data_source_is_untouched()
    {
        var abs = Path.Combine(Path.GetTempPath(), "explicit.db");
        var src = new SqliteConnectionStringBuilder(DatabaseOptions.AnchorSqliteFile($"Data Source={abs}")).DataSource;
        Assert.Equal(abs, src);
    }
}
