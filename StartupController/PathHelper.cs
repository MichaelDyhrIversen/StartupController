using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StartupController
{
    // Path comparisons used by the self-launch guard (ProgramLauncher.IsSelf)
    internal static class PathHelper
    {
        // "\\?\C:\x" -> "C:\x", "\\?\UNC\srv\share" -> "\\srv\share", "\\.\C:\x" -> "C:\x"
        internal static string StripDevicePrefix(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                return path.Substring(4);
            return path;
        }

        // Full path with 8.3 short names expanded (GetLongPathNameW works only for paths that exist)
        internal static string NormalizePath(string path)
        {
            var full = Path.GetFullPath(path);
            var buffer = new StringBuilder(1024);
            uint length = NativeMethods.GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
            if (length > buffer.Capacity)
            {
                buffer = new StringBuilder((int)length);
                length = NativeMethods.GetLongPathNameW(full, buffer, (uint)buffer.Capacity);
            }
            return length > 0 && length <= buffer.Capacity ? buffer.ToString() : full;
        }

        // Same volume serial number and file index: the same file, whatever path reaches it
        internal static bool IsSameFile(string a, string b)
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            if (!TryGetFileId(a, out var idA) || !TryGetFileId(b, out var idB)) return false;
            return idA == idB;
        }

        private static bool TryGetFileId(string path, out (uint Volume, uint High, uint Low) id)
        {
            id = default;
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!NativeMethods.GetFileInformationByHandle(handle, out var info)) return false;
            id = (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
            return true;
        }
    }
}
