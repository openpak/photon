using BlackIce.Tools.MapExtractor;

namespace MapExtractor.Tests;

/// <summary>
/// The clean-room gate: the extracted navmesh is game-derived, so the tool must never write it into the git
/// tree. These cover the allow/deny rules on synthetic paths (no I/O), the testable core of the guard.
/// </summary>
public sealed class OutputPathGuardTests
{
    private static string Repo => Path.Combine(Path.GetTempPath(), "blackice-repo-fixture");

    [Fact]
    public void AllowsPathUnderMapsDir()
    {
        string outPath = Path.Combine(Repo, "maps", "level13.navmesh");
        Assert.True(OutputPathGuard.IsAllowed(outPath, Repo, out _));
    }

    [Fact]
    public void RefusesTrackedPathInsideRepo()
    {
        string outPath = Path.Combine(Repo, "server", "level13.navmesh");
        Assert.False(OutputPathGuard.IsAllowed(outPath, Repo, out string reason));
        Assert.Contains("git tree", reason);
    }

    [Fact]
    public void RefusesRepoRootFile()
    {
        string outPath = Path.Combine(Repo, "level13.navmesh");
        Assert.False(OutputPathGuard.IsAllowed(outPath, Repo, out _));
    }

    [Fact]
    public void AllowsAbsolutePathOutsideRepo()
    {
        string outside = Path.Combine(Path.GetTempPath(), "scratch", "level13.navmesh");
        Assert.True(OutputPathGuard.IsAllowed(outside, Repo, out _));
    }

    [Fact]
    public void AllowsAnythingWhenNoRepoContext()
    {
        Assert.True(OutputPathGuard.IsAllowed("anywhere.navmesh", repoRoot: null, out _));
    }

    [Fact]
    public void RefusesEmptyPath()
    {
        Assert.False(OutputPathGuard.IsAllowed("", Repo, out _));
    }

    [Fact]
    public void MapsSubdirectoryNotSpoofedByPrefix()
    {
        // "maps-secret" must NOT be treated as inside "maps/".
        string sneaky = Path.Combine(Repo, "maps-secret", "x.navmesh");
        Assert.False(OutputPathGuard.IsAllowed(sneaky, Repo, out _));
    }
}
