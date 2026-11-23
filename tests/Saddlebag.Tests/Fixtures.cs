using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using Saddlebag.Core;
using Saddlebag.Core.Safety;

namespace Saddlebag.Tests;

public sealed class FakeGuard : IGameGuard
{
    public List<string> Running { get; } = [];
    public IReadOnlyList<string> RunningBlockers() => Running;
}

/// <summary>A throwaway game folder, Saddlebag home and fixture folder per test.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "saddlebag-tests", Guid.NewGuid().ToString("N")[..12]);
    public string Game => Path.Combine(Root, "game");
    public string Home => Path.Combine(Root, "home");
    public string Src => Path.Combine(Root, "src");
    public FakeGuard Guard { get; } = new();

    public Sandbox()
    {
        Directory.CreateDirectory(Game);
        Directory.CreateDirectory(Src);
        // A miniature vanilla install: root archives, the 12on7 folder, and a nested data file.
        Write(Game, "RDR2.exe", "vanilla exe");
        Write(Game, "anim_0.rpf", "anim archive");
        Write(Game, "hd_0.rpf", "hd archive");
        Write(Game, "12on7/online.rpf", "online archive");
        Write(Game, "x64/data/vanilla.meta", "vanilla meta");
        Write(Game, "bink2w64.dll", "vanilla bink");
    }

    public Workspace Init() => Workspace.Init(Home, Game, Guard);
    public Workspace Open() => Workspace.Open(Home, Guard);

    public static string Write(string root, string rel, string content)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    public static void WriteBytes(string root, string rel, byte[] content)
    {
        var p = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, content);
    }

    /// <summary>Builds a folder of files under src/name and returns its path.</summary>
    public string Folder(string name, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(Src, name);
        foreach (var (p, c) in files) Write(dir, p, c);
        return dir;
    }

    /// <summary>Builds a zip under src/name.zip.</summary>
    public string Zip(string name, params (string Path, string Content)[] files)
    {
        var dir = Folder(name + "-staging", files);
        var zip = Path.Combine(Src, name + ".zip");
        ZipFile.CreateFromDirectory(dir, zip);
        Directory.Delete(dir, true);
        return zip;
    }

    /// <summary>Imports and enables Script Hook RDR2, its .NET hook and (optionally) LML, so dependency checks pass.</summary>
    public void EnableFrameworks(Workspace ws, bool lml = false)
    {
        var hooks = Zip("scripthook-" + Guid.NewGuid().ToString("N")[..6],
            ("ScriptHookRDR2.dll", "hook"), ("dinput8.dll", "loader"), ("ScriptHookRDRDotNet.asi", "dotnet"), ("ScriptHookRDRNetAPI.dll", "api"));
        ws.Store.SetEnabled(ws.Library.Import(hooks).Mod.Id, true);
        if (lml)
            ws.Store.SetEnabled(ws.Library.Import(Zip("lml-" + Guid.NewGuid().ToString("N")[..6], ("vfs.asi", "vfs"), ("lml/mods.xml", "<mods/>"))).Mod.Id, true);
    }

    public string GameFile(string rel) => Path.Combine(Game, rel.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

/// <summary>Real PE files for DLL inspection tests, generated once per run.</summary>
public static class Dlls
{
    private static readonly Lazy<(byte[] Api, byte[] Script, byte[] Plain)> Built = new(Build);

    /// <summary>A managed assembly named ScriptHookRDRNetAPI.</summary>
    public static byte[] Api => Built.Value.Api;
    /// <summary>A managed assembly that references ScriptHookRDRNetAPI, like a real .NET script.</summary>
    public static byte[] Script => Built.Value.Script;
    /// <summary>A managed assembly with no reference to the RDR2 API.</summary>
    public static byte[] Plain => Built.Value.Plain;
    /// <summary>Bytes that aren't a PE at all; stands in for native or broken DLLs.</summary>
    public static byte[] Junk => "MZ not really a pe"u8.ToArray();

    private static (byte[], byte[], byte[]) Build()
    {
        var dir = Path.Combine(Path.GetTempPath(), "saddlebag-tests", "dlls-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var api = new PersistedAssemblyBuilder(new AssemblyName("ScriptHookRDRNetAPI"), typeof(object).Assembly);
        var mod = api.DefineDynamicModule("ScriptHookRDRNetAPI");
        mod.DefineType("RDR2.Script", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        var apiPath = Path.Combine(dir, "ScriptHookRDRNetAPI.dll");
        api.Save(apiPath);

        var loaded = Assembly.LoadFile(apiPath);
        var script = new PersistedAssemblyBuilder(new AssemblyName("HorsePersonalities"), typeof(object).Assembly);
        var smod = script.DefineDynamicModule("HorsePersonalities");
        smod.DefineType("HorsePersonalities.Main", TypeAttributes.Public | TypeAttributes.Class, loaded.GetType("RDR2.Script")).CreateType();
        var scriptPath = Path.Combine(dir, "HorsePersonalities.dll");
        script.Save(scriptPath);

        var plain = new PersistedAssemblyBuilder(new AssemblyName("Helper"), typeof(object).Assembly);
        plain.DefineDynamicModule("Helper").DefineType("Helper.Util", TypeAttributes.Public).CreateType();
        var plainPath = Path.Combine(dir, "Helper.dll");
        plain.Save(plainPath);

        return (File.ReadAllBytes(apiPath), File.ReadAllBytes(scriptPath), File.ReadAllBytes(plainPath));
    }
}
