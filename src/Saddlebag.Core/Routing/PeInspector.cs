using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Saddlebag.Core.Routing;

public enum DllKind { NotPe, Native, Managed }

/// <summary>What a DLL is. Whether it's a .NET script is decided by the router, from the rules' API name prefixes.</summary>
public sealed record DllInfo(DllKind Kind, IReadOnlyList<string> AssemblyReferences);

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
