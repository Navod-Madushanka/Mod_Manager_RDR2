using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Saddlebag.Core.Util;

public static partial class FileOps
{
    public static string Sha256(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(s));
    }

    public static string Sha256(Stream s) => Convert.ToHexStringLower(SHA256.HashData(s));

    /// <summary>Last write time in UTC ticks; the cheap half of the "size and mtime first" check.</summary>
    public static long MTime(string path) => File.GetLastWriteTimeUtc(path).Ticks;

    /// <summary>
    /// Creates a hard link at <paramref name="link"/> pointing at <paramref name="target"/>.
    /// Returns false when the OS refuses (different volume, unsupported file system), so callers can copy instead.
    /// </summary>
    public static bool TryHardLink(string target, string link)
    {
        if (OperatingSystem.IsWindows())
            return CreateHardLinkW(link, target, IntPtr.Zero);
        return link_unix(target, link) == 0;
    }

    /// <summary>Copies via a temp file and rename, so a crash never leaves a half-written file at the destination.</summary>
    public static void AtomicCopy(string source, string dest)
    {
        var tmp = dest + ".saddlebag-tmp";
        File.Copy(source, tmp, overwrite: true);
        File.Move(tmp, dest, overwrite: false);
    }

    public static void EnsureParent(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int link_unix(string oldpath, string newpath);
}
