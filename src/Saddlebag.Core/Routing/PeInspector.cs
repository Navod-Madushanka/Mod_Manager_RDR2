using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Saddlebag.Core.Routing;

public enum DllKind { NotPe, Native, Managed }

public sealed record DllInfo(DllKind Kind, IReadOnlyList<string> AssemblyReferences)
{
    /// <summary>A managed DLL that references the Script Hook RDR2 .NET API is a script for the scripts/ folder.</summary>
    public bool IsRdrDotNetScript => Kind == DllKind.Managed &&
        AssemblyReferences.Any(r => r.Contains("ScriptHookRDR", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the PE header instead of trusting the extension: native vs managed, and what a managed DLL references.</summary>
public static class PeInspector
{
    public static DllInfo Inspect(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            if (!pe.HasMetadata) return new DllInfo(DllKind.Native, []);
            var md = pe.GetMetadataReader();
            var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
            return new DllInfo(DllKind.Managed, refs);
        }
        catch (BadImageFormatException)
        {
            return new DllInfo(DllKind.NotPe, []);
        }
    }
}
