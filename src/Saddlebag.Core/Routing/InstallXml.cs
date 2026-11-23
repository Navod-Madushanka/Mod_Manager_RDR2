using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Saddlebag.Core.Routing;

/// <summary>One &lt;FileReplacement&gt;: a game path (with its mount, e.g. "update:/x64/...") and the file inside the package.</summary>
public sealed record FileReplacement(string GamePath, string FilePath);

public sealed record InstallXmlInfo(string? Name, string? Author, string? Version, IReadOnlyList<string> StreamingFolders, IReadOnlyList<FileReplacement> Replacements);

/// <summary>
/// LML's install.xml as RDR2 mods use it. Format taken from real RDR2 packages on GitHub:
/// &lt;EasyInstall&gt; with Name, Author, Version, Link, Metadata, then Resources/Resource holding
/// &lt;StreamingFiles&gt;folder&lt;/StreamingFiles&gt; and &lt;FileReplacement&gt;&lt;GamePath/&gt;&lt;FilePath/&gt;&lt;/FileReplacement&gt;.
/// </summary>
public static partial class InstallXml
{
    /// <summary>A game path is a mount ("update", "dlc_mp009", …), a colon and slash, then the path inside it. One-letter mounts are drive letters, not mounts.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_]{2,}:/[^\s<>""|?*]+$")]
    private static partial Regex GamePathShape();

    public static bool IsGamePath(string s) => GamePathShape().IsMatch(s) && !s.Contains("/../") && !s.EndsWith('/');

    public static string Generate(string name, string? author, string? version, string? link, string? metadata,
        string? streamingFolder, IEnumerable<FileReplacement> replacements)
    {
        var resource = new XElement("Resource");
        if (streamingFolder is not null) resource.Add(new XElement("StreamingFiles", streamingFolder));
        foreach (var r in replacements)
            resource.Add(new XElement("FileReplacement", new XElement("GamePath", r.GamePath), new XElement("FilePath", r.FilePath)));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("EasyInstall",
                new XElement("Name", name),
                new XElement("Author", author ?? "Unknown"),
                new XElement("Version", version ?? "1.0"),
                new XElement("Link", link ?? ""),
                new XElement("Metadata", metadata ?? ""),
                new XElement("Resources", resource)));
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString().Replace("\r\n", "\n") + "\n";
    }

    /// <summary>Reads an install.xml leniently (real files have stray characters and comments). Returns null if it isn't XML.</summary>
    public static InstallXmlInfo? Read(string path)
    {
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch (XmlException) { return null; }
        catch (IOException) { return null; }
        var root = doc.Root;
        if (root is null) return null;
        string? Child(string n) => root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(n, StringComparison.OrdinalIgnoreCase))?.Value.Trim();
        var resources = root.Descendants().Where(e => e.Name.LocalName.Equals("Resource", StringComparison.OrdinalIgnoreCase)).ToList();
        var streams = resources.SelectMany(r => r.Elements())
            .Where(e => e.Name.LocalName.Equals("StreamingFiles", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToList();
        var reps = resources.SelectMany(r => r.Elements())
            .Where(e => e.Name.LocalName.Equals("FileReplacement", StringComparison.OrdinalIgnoreCase))
            .Select(e => new FileReplacement(
                e.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("GamePath", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? "",
                e.Elements().FirstOrDefault(x => x.Name.LocalName.Equals("FilePath", StringComparison.OrdinalIgnoreCase))?.Value.Trim() ?? ""))
            .Where(r => r.GamePath.Length > 0)
            .ToList();
        return new InstallXmlInfo(Child("Name"), Child("Author"), Child("Version"), streams, reps);
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override System.Text.Encoding Encoding => new System.Text.UTF8Encoding(false);
    }
}
