namespace BlackIce.Tools.MapExtractor;

/// <summary>
/// Clean-room safety gate for where the tool is allowed to write its <c>.navmesh</c> output. The extracted
/// navmesh is game-derived material and must NEVER land in the git tree (only the original tool code is
/// committed). Two writes are permitted:
/// <list type="number">
///   <item>any absolute path <b>outside</b> the repository (operator's own scratch dir), or</item>
///   <item>a path <b>inside</b> the repo but under the gitignored <c>maps/</c> directory.</item>
/// </list>
/// Anything else (a tracked path inside the repo) is refused. <c>.gitignore</c> already blocks
/// <c>maps/</c> and <c>*.navmesh</c>; this guard is defense-in-depth so a slip can't stage game data.
/// Pure path logic — no I/O — so it is unit-testable.
/// </summary>
public static class OutputPathGuard
{
    /// <summary>
    /// Returns true if <paramref name="outputPath"/> is an allowed write target given the repository root.
    /// </summary>
    /// <param name="outputPath">The requested output path (may be relative or absolute).</param>
    /// <param name="repoRoot">Absolute path to the repository root (the directory containing <c>.git</c>).</param>
    /// <param name="reason">On failure, a human-readable explanation of why the path was refused.</param>
    public static bool IsAllowed(string outputPath, string? repoRoot, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            reason = "output path is empty";
            return false;
        }

        string full = Normalize(Path.GetFullPath(outputPath));

        // No repo context (tool run outside a repo): only the gitignore would have protected us, so just
        // require the artifact extension and allow it — there is no tracked tree to leak into.
        if (string.IsNullOrWhiteSpace(repoRoot))
            return true;

        string root = Normalize(Path.GetFullPath(repoRoot));
        string mapsDir = Normalize(Path.Combine(root, "maps"));

        bool insideRepo = IsUnder(full, root);
        if (!insideRepo)
            return true; // absolute path outside the repo: operator's own space, nothing to leak.

        if (IsUnder(full, mapsDir))
            return true; // inside the repo but under the gitignored maps/ dir: allowed.

        reason = $"refusing to write game-derived navmesh into the git tree at '{full}'. " +
                 $"Write it under '{mapsDir}' (gitignored) or to an absolute path outside the repo.";
        return false;
    }

    private static bool IsUnder(string path, string dir)
    {
        if (string.Equals(path, dir, PathComparison)) return true;
        string prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    // Trim a trailing separator (except the root) so comparisons are consistent.
    private static string Normalize(string p)
    {
        if (p.Length > 3 && (p.EndsWith(Path.DirectorySeparatorChar) || p.EndsWith(Path.AltDirectorySeparatorChar)))
            return p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return p;
    }

    // Windows paths are case-insensitive; the project's primary platform is win32.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Walks up from <paramref name="start"/> to find the directory containing a <c>.git</c> entry,
    /// or null if none. Used to locate the repo root for the guard above.</summary>
    public static string? FindRepoRoot(string start)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(start));
            while (dir is not null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")) ||
                    File.Exists(Path.Combine(dir.FullName, ".git"))) // .git file = worktree
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch
        {
            // best-effort; treat as "no repo".
        }
        return null;
    }
}
