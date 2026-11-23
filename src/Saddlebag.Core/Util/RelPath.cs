namespace Saddlebag.Core.Util;

/// <summary>
/// Relative paths are stored with '/' separators and compared case-insensitively,
/// because the game runs on Windows where the file system ignores case.
/// </summary>
public static class RelPath
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public static string Normalize(string path)
    {
        var p = path.Replace('\\', '/').Trim('/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        return p;
    }

    /// <summary>Relative path from root to full, normalized.</summary>
    public static string From(string root, string full) => Normalize(Path.GetRelativePath(root, full));

    /// <summary>Absolute OS path for a relative path under root. Rejects anything that escapes root.</summary>
    public static string ToFull(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root);
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path '{rel}' escapes '{root}'.");
        return full;
    }

    /// <summary>True if the relative path is safe to place under a root (no rooted paths, no '..' segments).</summary>
    public static bool IsSafe(string rel)
    {
        var n = Normalize(rel);
        if (n.Length == 0 || Path.IsPathRooted(rel) || n.Contains(':')) return false;
        return n.Split('/').All(s => s != ".." && s != ".");
    }

    public static string FileName(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? rel : rel[(i + 1)..];
    }

    public static string Dir(string rel)
    {
        var i = rel.LastIndexOf('/');
        return i < 0 ? "" : rel[..i];
    }

    public static string Combine(string a, string b) =>
        a.Length == 0 ? Normalize(b) : b.Length == 0 ? Normalize(a) : Normalize(a + "/" + b);

    /// <summary>True if rel is inside dir (or dir is the root "").</summary>
    public static bool IsUnder(string rel, string dir) =>
        dir.Length == 0 || rel.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase);

    public static string StripPrefix(string rel, string dir) => dir.Length == 0 ? rel : rel[(dir.Length + 1)..];

    public static string FirstSegment(string rel)
    {
        var i = rel.IndexOf('/');
        return i < 0 ? rel : rel[..i];
    }
}
