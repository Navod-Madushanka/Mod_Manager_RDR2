namespace Saddlebag.Core;

/// <summary>
/// Where Saddlebag's folder (library, backups, state.db) is: an explicit path, then $SADDLEBAG_HOME, then the folder
/// remembered by the last `init`. Shared by the CLI and the GUI so both open the same state.
/// </summary>
public static class HomeLocator
{
    public static string PointerFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Saddlebag", "home.txt");

    /// <summary>The home folder, or null if none has been set up yet.</summary>
    public static string? TryResolve(string? explicitHome = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitHome)) return Path.GetFullPath(explicitHome);
        var env = Environment.GetEnvironmentVariable("SADDLEBAG_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
        return File.Exists(PointerFile) ? File.ReadAllText(PointerFile).Trim() : null;
    }

    public static void Remember(string home)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
        File.WriteAllText(PointerFile, home);
    }
}
