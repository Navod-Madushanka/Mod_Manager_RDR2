using System.Diagnostics;

namespace Saddlebag.Core.Safety;

public interface IGameGuard
{
    /// <summary>Names of game or launcher processes that are running right now.</summary>
    IReadOnlyList<string> RunningBlockers();
}

public sealed class GameRunningException(IReadOnlyList<string> processes)
    : Exception($"Close these first: {string.Join(", ", processes)}. Saddlebag never touches files while the game or launcher is open.")
{
    public IReadOnlyList<string> Processes { get; } = processes;
}

/// <summary>Blocks every file operation while RDR2 or a launcher that can rewrite game files is open.</summary>
public sealed class ProcessGameGuard : IGameGuard
{
    // Process names without ".exe". "Launcher" is the Rockstar Games Launcher's generic process name,
    // so it is only counted when its path points at Rockstar.
    private static readonly string[] Always = ["RDR2", "PlayRDR2", "RockstarService", "SocialClubHelper", "RockstarErrorHandler"];

    public IReadOnlyList<string> RunningBlockers()
    {
        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (Always.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase))
                    found.Add(p.ProcessName);
                else if (p.ProcessName.Equals("Launcher", StringComparison.OrdinalIgnoreCase) && IsRockstar(p))
                    found.Add("Rockstar Games Launcher");
            }
            finally { p.Dispose(); }
        }
        return found.Distinct().ToList();
    }

    private static bool IsRockstar(Process p)
    {
        try { return p.MainModule?.FileName.Contains("Rockstar", StringComparison.OrdinalIgnoreCase) ?? true; }
        catch { return true; } // can't read the path: assume it's the launcher and block, rather than risk it
    }
}

public static class GuardExtensions
{
    public static void ThrowIfRunning(this IGameGuard guard)
    {
        var running = guard.RunningBlockers();
        if (running.Count > 0) throw new GameRunningException(running);
    }
}
