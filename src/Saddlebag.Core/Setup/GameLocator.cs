using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Saddlebag.Core.Setup;

public enum GameStore { Steam, Epic, Rockstar, Unknown }

/// <summary>An RDR2 install, and how to start or repair it through its launcher.</summary>
public sealed record GameInstall(string Folder, GameStore Store, string? EpicAppName = null)
{
    public const string SteamAppId = "1174180";

    public string StoreName => Store switch
    {
        GameStore.Steam => "Steam",
        GameStore.Epic => "Epic Games",
        GameStore.Rockstar => "Rockstar Games Launcher",
        _ => "your launcher",
    };

    /// <summary>Opens the launcher's "verify files" directly where it has a link for it (Steam does).</summary>
    public string? VerifyUri => Store == GameStore.Steam ? $"steam://validate/{SteamAppId}" : null;

    /// <summary>Starts the game through its launcher.</summary>
    public string? LaunchUri => Store switch
    {
        GameStore.Steam => $"steam://rungameid/{SteamAppId}",
        GameStore.Epic when EpicAppName is not null => $"com.epicgames.launcher://apps/{EpicAppName}?action=launch&silent=true",
        _ => null,
    };

    /// <summary>For launchers without a link: the executable that starts the game (PlayRDR2.exe hands over to the Rockstar launcher).</summary>
    public string? LaunchExe => LaunchUri is null && File.Exists(Path.Combine(Folder, "PlayRDR2.exe")) ? Path.Combine(Folder, "PlayRDR2.exe") : null;

    /// <summary>Where to click when there's no direct link.</summary>
    public string VerifyHelp => Store switch
    {
        GameStore.Steam => "Steam opens and checks the game files. Wait until it says all files validated.",
        GameStore.Epic => "In Epic Games: Library → the ⋯ under Red Dead Redemption 2 → Manage → Verify. Wait until it finishes.",
        GameStore.Rockstar => "In the Rockstar Games Launcher: Settings → My installed games → Red Dead Redemption 2 → Verify Integrity. Wait until it finishes.",
        _ => "Use your launcher's \"verify\" or \"repair\" option for Red Dead Redemption 2 and wait until it finishes.",
    };
}

/// <summary>What the locator reads: files and (on Windows) a few registry values. Swappable so tests can fake an install.</summary>
public interface IInstallProbe
{
    string? SteamPath();
    string? RockstarInstallFolder();
    string EpicManifestsDir();
    IEnumerable<string> FixedDriveRoots();
}

public sealed class WindowsInstallProbe : IInstallProbe
{
    public string? SteamPath() => OperatingSystem.IsWindows()
        ? Registry(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath") ?? DefaultIfExists(@"C:\Program Files (x86)\Steam")
        : null;

    public string? RockstarInstallFolder() => OperatingSystem.IsWindows()
        ? Registry(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games\Red Dead Redemption 2", "InstallFolder")
          ?? Registry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Rockstar Games\Red Dead Redemption 2", "InstallFolder")
          ?? DefaultIfExists(@"C:\Program Files\Rockstar Games\Red Dead Redemption 2")
        : null;

    public string EpicManifestsDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public IEnumerable<string> FixedDriveRoots() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.RootDirectory.FullName);

    private static string? Registry(string key, string value)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Microsoft.Win32.Registry.GetValue(key, value, null) as string; }
        catch { return null; }
    }

    private static string? DefaultIfExists(string path) => Directory.Exists(path) ? path : null;
}

/// <summary>
/// Finds RDR2 without asking: Steam libraries (libraryfolders.vdf), Epic's install manifests, the Rockstar launcher's
/// install folder, and the usual folders on each drive. Every candidate must contain RDR2.exe.
/// </summary>
public static partial class GameLocator
{
    private const string GameExe = "RDR2.exe";

    [GeneratedRegex("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"installdir\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfInstallDir();

    private static string Unescape(string vdf) => vdf.Replace(@"\\", @"\");

    public static List<GameInstall> FindAll(IInstallProbe? probe = null)
    {
        probe ??= new WindowsInstallProbe();
        var found = new List<GameInstall>();
        void Add(GameInstall g)
        {
            if (!File.Exists(Path.Combine(g.Folder, GameExe))) return;
            var full = Path.GetFullPath(g.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (found.Any(f => string.Equals(f.Folder, full, StringComparison.OrdinalIgnoreCase))) return;
            found.Add(g with { Folder = full });
        }

        try { foreach (var g in Steam(probe)) Add(g); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { foreach (var g in Epic(probe)) Add(g); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (probe.RockstarInstallFolder() is { } r) Add(new GameInstall(r, GameStore.Rockstar));
        foreach (var root in probe.FixedDriveRoots())
            foreach (var rel in new[] { @"Program Files\Rockstar Games\Red Dead Redemption 2", @"Rockstar Games\Red Dead Redemption 2",
                                        @"SteamLibrary\steamapps\common\Red Dead Redemption 2", @"Program Files (x86)\Steam\steamapps\common\Red Dead Redemption 2",
                                        @"Epic Games\RedDeadRedemption2", @"Program Files\Epic Games\RedDeadRedemption2" })
            {
                var path = Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar));
                Add(new GameInstall(path, Classify(path)));
            }
        return found;
    }

    private static IEnumerable<GameInstall> Steam(IInstallProbe probe)
    {
        if (probe.SteamPath() is not { } steam) yield break;
        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => Unescape(m.Groups[1].Value)));
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            var acf = Path.Combine(apps, $"appmanifest_{GameInstall.SteamAppId}.acf");
            var dir = File.Exists(acf) && AcfInstallDir().Match(File.ReadAllText(acf)) is { Success: true } m
                ? Unescape(m.Groups[1].Value)
                : "Red Dead Redemption 2";
            yield return new GameInstall(Path.Combine(apps, "common", dir), GameStore.Steam);
        }
    }

    private static IEnumerable<GameInstall> Epic(IInstallProbe probe)
    {
        var dir = probe.EpicManifestsDir();
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            GameInstall? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                string? S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (S("InstallLocation") is { } loc &&
                    ((S("DisplayName") ?? "").Contains("Red Dead Redemption 2", StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(loc, GameExe))))
                    g = new GameInstall(loc, GameStore.Epic, S("AppName"));
            }
            catch (JsonException) { }
            if (g is not null) yield return g;
        }
    }

    /// <summary>Guesses the store of a folder you picked yourself, so the right repair and launch help is shown.</summary>
    public static GameInstall Describe(string folder, IInstallProbe? probe = null)
    {
        probe ??= new WindowsInstallProbe();
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var known = FindAll(probe).FirstOrDefault(g => string.Equals(g.Folder, full, StringComparison.OrdinalIgnoreCase));
        return known ?? new GameInstall(full, Classify(full));
    }

    private static GameStore Classify(string path) =>
        path.Contains("steamapps", StringComparison.OrdinalIgnoreCase) ? GameStore.Steam
        : path.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ? GameStore.Epic
        : path.Contains("Rockstar Games", StringComparison.OrdinalIgnoreCase) ? GameStore.Rockstar
        : GameStore.Unknown;

    /// <summary>A folder for Saddlebag's library on the game's drive, so hard links work: e.g. D:\Saddlebag.</summary>
    public static string SuggestHome(string gameFolder) =>
        Path.Combine(Path.GetPathRoot(Path.GetFullPath(gameFolder)) is { Length: > 0 } root ? root : gameFolder, "Saddlebag");

    /// <summary>True when Saddlebag can create files in the game folder (false under Program Files without admin rights).</summary>
    public static bool CanWrite(string gameFolder)
    {
        var probe = Path.Combine(gameFolder, $".saddlebag-write-{Environment.ProcessId}");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Opens a launcher link or file with Windows' default handler.</summary>
    public static void Open(string uriOrPath) => Process.Start(new ProcessStartInfo(uriOrPath) { UseShellExecute = true });
}
