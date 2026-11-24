using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Saddlebag.Core.Routing;

/// <summary>
/// The routing data: which file names are loaders and hooks, which extensions stream, which folders carry meaning, and
/// the title and explanation of every rule. Built in (rules.default.json); a rules.json in the Saddlebag folder can
/// override any key, so a name this build doesn't know (say, a renamed .NET hook) can be added without a new build.
/// </summary>
public sealed class RuleSet
{
    public IReadOnlyList<string> Frameworks { get; init; } = [];
    public IReadOnlyList<string> AsiLoaders { get; init; } = [];
    public IReadOnlyList<string> HookFiles { get; init; } = [];
    public IReadOnlyList<string> DotNetApiReferencePrefixes { get; init; } = [];
    public IReadOnlyList<string> StreamExtensions { get; init; } = [];
    public IReadOnlyList<string> DataExtensions { get; init; } = [];
    public IReadOnlyList<string> DocExtensions { get; init; } = [];
    public IReadOnlyList<string> StructuralFolders { get; init; } = [];
    public IReadOnlyList<string> SignatureFiles { get; init; } = [];
    public IReadOnlyList<string> SignatureFolders { get; init; } = [];
    /// <summary>Rule id → title, as shown in previews and `saddlebag rules`.</summary>
    public IReadOnlyDictionary<string, string> Rules { get; init; } = new Dictionary<string, string>();
    /// <summary>Reason key → the "why" text shown next to each routed file.</summary>
    public IReadOnlyDictionary<string, string> Reasons { get; init; } = new Dictionary<string, string>();

    /// <summary>"built-in", or the path of the rules.json that changed something.</summary>
    public string Source { get; init; } = "built-in";
    /// <summary>Fingerprint of the effective rules; when it changes, mods are re-routed.</summary>
    public string Hash { get; init; } = "";

    public string Reason(string key) => Reasons.TryGetValue(key, out var r) ? r : key;

    private static readonly string[] ListKeys =
        ["frameworks", "asiLoaders", "hookFiles", "dotNetApiReferencePrefixes", "streamExtensions", "dataExtensions",
         "docExtensions", "structuralFolders", "signatureFiles", "signatureFolders"];
    private static readonly string[] MapKeys = ["rules", "reasons"];

    public static string DefaultJson { get; } = ReadDefault();
    public static RuleSet Default { get; } = Build(JsonNode.Parse(DefaultJson)!.AsObject(), "built-in");

    private static string ReadDefault()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Saddlebag.Core.Routing.rules.default.json")
                      ?? throw new InvalidOperationException("Built-in rules are missing from this build.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>The built-in rules, with <paramref name="overridePath"/> (if it exists) applied on top.</summary>
    public static RuleSet Load(string? overridePath)
    {
        if (overridePath is null || !File.Exists(overridePath)) return Default;
        JsonObject user;
        try
        {
            user = JsonNode.Parse(File.ReadAllText(overridePath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
            }) as JsonObject ?? throw new InvalidOperationException("the file must hold one JSON object { ... }");
        }
        catch (JsonException e) { throw Bad(overridePath, $"isn't valid JSON ({e.Message})"); }
        catch (InvalidOperationException e) { throw Bad(overridePath, e.Message); }

        var merged = JsonNode.Parse(DefaultJson)!.AsObject();
        foreach (var (key, value) in user)
        {
            if (key.StartsWith('$')) continue; // comments
            if (ListKeys.Contains(key))
            {
                if (value is not JsonArray arr || arr.Any(v => v?.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(v.GetValue<string>())))
                    throw Bad(overridePath, $"\"{key}\" must be a list of non-empty strings");
                merged[key] = arr.DeepClone();
            }
            else if (MapKeys.Contains(key))
            {
                if (value is not JsonObject obj || obj.Any(kv => kv.Value?.GetValueKind() != JsonValueKind.String))
                    throw Bad(overridePath, $"\"{key}\" must map names to text");
                var target = merged[key]!.AsObject();
                foreach (var (k, v) in obj) target[k] = v!.DeepClone();
            }
            else throw Bad(overridePath, $"\"{key}\" isn't a rules setting. Known: {string.Join(", ", ListKeys.Concat(MapKeys))}");
        }
        var rules = Build(merged, overridePath);
        return rules.Hash == Default.Hash ? Default : rules;
    }

    private static InvalidOperationException Bad(string path, string why) => new($"Your rules file {path} {why}. Fix it, or delete it to use the built-in rules.");

    private static RuleSet Build(JsonObject o, string source)
    {
        List<string> L(string k) => o[k]!.AsArray().Select(v => v!.GetValue<string>().Trim()).ToList();
        Dictionary<string, string> M(string k) => o[k]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());

        foreach (var k in new[] { "streamExtensions", "dataExtensions", "docExtensions" })
            if (L(k).FirstOrDefault(e => !e.StartsWith('.')) is { } bad)
                throw new InvalidOperationException($"Rules file {source}: \"{k}\" entries must start with a dot, not \"{bad}\".");
        foreach (var k in new[] { "frameworks", "asiLoaders", "hookFiles", "signatureFiles", "structuralFolders", "signatureFolders" })
            if (L(k).FirstOrDefault(n => n.Contains('/') || n.Contains('\\')) is { } bad)
                throw new InvalidOperationException($"Rules file {source}: \"{k}\" holds names, not paths (\"{bad}\").");

        var canonical = string.Join("\n", ListKeys.Select(k => k + "=" + string.Join("|", L(k)))
            .Concat(MapKeys.SelectMany(k => M(k).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{k}.{kv.Key}={kv.Value}"))));
        return new RuleSet
        {
            Frameworks = L("frameworks"), AsiLoaders = L("asiLoaders"), HookFiles = L("hookFiles"),
            DotNetApiReferencePrefixes = L("dotNetApiReferencePrefixes"),
            StreamExtensions = L("streamExtensions"), DataExtensions = L("dataExtensions"), DocExtensions = L("docExtensions"),
            StructuralFolders = L("structuralFolders"), SignatureFiles = L("signatureFiles"), SignatureFolders = L("signatureFolders"),
            Rules = M("rules"), Reasons = M("reasons"),
            Source = source,
            Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16],
        };
    }
}
