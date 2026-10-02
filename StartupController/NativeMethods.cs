using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StartupController
{
    // Win32 declarations used by the app. Kept in one place so callers stay free of interop details.
    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetLongPathNameW(string lpszShortPath, StringBuilder lpszLongPath, uint cchBuffer);
    }
}
