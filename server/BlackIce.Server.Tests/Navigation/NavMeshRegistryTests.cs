using System.IO;
using BlackIce.Server.Core.Navigation;
using BlackIce.Server.LoadBalancing.Navigation;
using Xunit;

namespace BlackIce.Server.Tests.Navigation;

/// <summary>
/// The DI-singleton navmesh cache the world-aware bots consult. Tested against a temp <c>maps/</c> directory
/// (the explicit-dir constructor) with a synthetic mesh written via <see cref="NavMeshFile.Write"/> — no game
/// asset required. Confirms the graceful-absence contract (missing map → null) and the cache (same instance
/// on repeat, missing map not re-stat'd).
/// </summary>
public class NavMeshRegistryTests
{
    private static NavMesh Strip()
    {
        // A flat 2x1 quad on the XZ plane at y=0, two triangles sharing the diagonal.
        float[] verts = { 0, 0, 0, 1, 0, 0, 0, 0, 1, 1, 0, 1 };
        int[] tris = { 0, 1, 2, 1, 3, 2 };
        return new NavMesh(verts, tris);
    }

    private static string FreshMapsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "blackice-navmesh-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Missing_map_returns_null()
    {
        var reg = new NavMeshRegistry(FreshMapsDir());
        Assert.Null(reg.For("level13"));
    }

    [Fact]
    public void Null_or_blank_map_name_returns_null()
    {
        var reg = new NavMeshRegistry(FreshMapsDir());
        Assert.Null(reg.For(null));
        Assert.Null(reg.For(""));
        Assert.Null(reg.For("   "));
    }

    [Fact]
    public void Written_then_loaded_navmesh_is_non_null_and_cached()
    {
        var dir = FreshMapsDir();
        using (var fs = File.Create(Path.Combine(dir, "level13.navmesh")))
            NavMeshFile.Write(fs, Strip());

        var reg = new NavMeshRegistry(dir);
        var first = reg.For("level13");
        Assert.NotNull(first);
        Assert.Equal(2, first!.TriangleCount);

        // Cached: the SAME instance comes back on a second call (no re-read of the file).
        var second = reg.For("level13");
        Assert.Same(first, second);
    }

    [Fact]
    public void Missing_map_is_cached_so_a_later_drop_in_is_not_picked_up()
    {
        // The cache stores the "loaded nothing" result too, so a missing map is resolved once (not stat'd per
        // bot tick). Dropping a file in AFTER the first miss does not change the cached null — documents the
        // load-once contract (the server resolves maps at startup, not mid-run).
        var dir = FreshMapsDir();
        var reg = new NavMeshRegistry(dir);
        Assert.Null(reg.For("level13"));

        using (var fs = File.Create(Path.Combine(dir, "level13.navmesh")))
            NavMeshFile.Write(fs, Strip());
        Assert.Null(reg.For("level13"));   // still the cached null
    }

    [Fact]
    public void Default_maps_directory_is_resolved_under_the_base_directory()
    {
        var reg = new NavMeshRegistry((string?)null);
        Assert.Equal(Path.Combine(System.AppContext.BaseDirectory, "maps"), reg.MapsDirectory);
    }
}
