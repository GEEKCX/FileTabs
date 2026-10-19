using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FileTabs;

/// <summary>
/// One-time import of 360 Folder's data files (formats recovered by static
/// reverse engineering of 360FileBrowser v1.0.0.1150):
///
/// favoriates.dat: header 0x09, then per bookmark:
///   0x10 0x11 &lt;u32 pidlLen&gt; PIDL 0x12 &lt;u32 nameLen&gt; UTF-16 name
/// lasttabs.dat:   header 0x29, then per tab:
///   0x30 0x31 &lt;u32 pidlLen&gt; PIDL 0x32 &lt;u32 4&gt; &lt;u32 flags&gt;
///
/// PIDLs are converted to plain paths so the rest of the app stays
/// path-based.
/// </summary>
public static class Import360
{
    private static string ProfileDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "360FileBrowser", "Profile");

    public static bool Has360Data =>
        File.Exists(Path.Combine(ProfileDirectory, "favoriates.dat")) ||
        File.Exists(Path.Combine(ProfileDirectory, "lasttabs.dat"));

    public static List<Bookmark> ImportBookmarks()
    {
        var result = new List<Bookmark>();
        var file = Path.Combine(ProfileDirectory, "favoriates.dat");
        if (!File.Exists(file)) return result;

        var bytes = File.ReadAllBytes(file);
        if (bytes.Length < 1 || bytes[0] != 0x09) return result;

        var pos = 1;
        while (pos + 2 <= bytes.Length && bytes[pos] == 0x10)
        {
            try
            {
                pos++; // 0x10
                if (bytes[pos] != 0x11) break;
                pos++; // 0x11
                var pidlLen = BitConverter.ToUInt32(bytes, pos);
                pos += 4;
                var pidl = bytes.AsSpan(pos, (int)pidlLen).ToArray();
                pos += (int)pidlLen;
                if (bytes[pos] != 0x12) break;
                pos++; // 0x12
                var nameLen = BitConverter.ToUInt32(bytes, pos);
                pos += 4;
                var name = Encoding.Unicode.GetString(bytes, pos, (int)nameLen).TrimEnd('\0');
                pos += (int)nameLen;

                var path = PidlToPath(pidl);
                if (!string.IsNullOrEmpty(path))
                {
                    result.Add(new Bookmark
                    {
                        Name = name.Length > 0 ? name : path,
                        Path = path,
                        IsFolder = Directory.Exists(path),
                    });
                }
            }
            catch (Exception)
            {
                // Truncated or corrupt tail: keep what was parsed so far.
                break;
            }
        }
        return result;
    }

    public static List<string> ImportSessionPaths()
    {
        var result = new List<string>();
        var file = Path.Combine(ProfileDirectory, "lasttabs.dat");
        if (!File.Exists(file)) return result;

        var bytes = File.ReadAllBytes(file);
        if (bytes.Length < 1 || bytes[0] != 0x29) return result;

        var pos = 1;
        while (pos + 2 <= bytes.Length && bytes[pos] == 0x30)
        {
            try
            {
                pos++; // 0x30
                if (bytes[pos] != 0x31) break;
                pos++; // 0x31
                var pidlLen = BitConverter.ToUInt32(bytes, pos);
                pos += 4;
                var pidl = bytes.AsSpan(pos, (int)pidlLen).ToArray();
                pos += (int)pidlLen;
                if (bytes[pos] != 0x32) break;
                pos++; // 0x32
                var flagLen = BitConverter.ToUInt32(bytes, pos);
                pos += 4 + (int)flagLen; // 4-byte selected flag, not needed

                var path = PidlToPath(pidl);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
            catch (Exception)
            {
                break;
            }
        }
        return result;
    }

    private static string? PidlToPath(byte[] pidl)
    {
        if (pidl.Length < 2) return null;

        var handle = GCHandle.Alloc(pidl, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            var buffer = new StringBuilder(1024);
            if (SHGetPathFromIDListW(ptr, buffer)) return buffer.ToString();

            // Not an absolute PIDL: prepend the desktop folder PIDL.
            if (SHGetSpecialFolderLocation(IntPtr.Zero, CSIDL_DESKTOP, out var desktop) == 0)
            {
                var combined = ILCombine(desktop, ptr);
                try
                {
                    if (combined != IntPtr.Zero && SHGetPathFromIDListW(combined, buffer))
                    {
                        return buffer.ToString();
                    }
                }
                finally
                {
                    if (combined != IntPtr.Zero) ILFree(combined);
                    ILFree(desktop);
                }
            }
            return null;
        }
        finally
        {
            handle.Free();
        }
    }

    private const int CSIDL_DESKTOP = 0;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder path);

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);
}
