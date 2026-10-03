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

        // --- Remote Desktop Services (WtsLogonSessionKeyProvider, 4.D8) ---

        internal static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
        internal const uint WTS_CURRENT_SESSION = uint.MaxValue; // (DWORD)-1
        internal const int WTSSessionInfo = 24;                  // WTS_INFO_CLASS value for WTSINFOW

        // Lengths from WtsApi32.h (SDK 10.0.19041): WINSTATIONNAME_LENGTH 32, DOMAIN_LENGTH 17, USERNAME_LENGTH 20
        internal const int WINSTATIONNAME_LENGTH = 32;
        internal const int DOMAIN_LENGTH = 17;
        internal const int USERNAME_LENGTH = 20;

        // WTSINFOW from WtsApi32.h. The times are LARGE_INTEGERs. Size 216 bytes, LogonTime at offset 200.
        // The three WCHAR arrays are opaque padding (raw UTF-16 code units, never decoded): only SessionId and
        // LogonTime are used, so the station, domain and user names are never marshalled as text.
        // Only WtsLogonSessionKeyProvider uses this struct; it lives here with the P/Invoke that returns it.
        [StructLayout(LayoutKind.Sequential)]
        internal struct WTSINFOW
        {
            public int State;
            public uint SessionId;
            public uint IncomingBytes;
            public uint OutgoingBytes;
            public uint IncomingFrames;
            public uint OutgoingFrames;
            public uint IncomingCompressedBytes;
            public uint OutgoingCompressedBytes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = WINSTATIONNAME_LENGTH)]
            public ushort[] WinStationName;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = DOMAIN_LENGTH)]
            public ushort[] Domain;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = USERNAME_LENGTH + 1)]
            public ushort[] UserName;
            public long ConnectTime;
            public long DisconnectTime;
            public long LastInputTime;
            public long LogonTime;
            public long CurrentTime;
        }

        // wtsapi32 is not a KnownDLL, so it is loaded from System32 only (never from the app or current directory)
        [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

        [DllImport("wtsapi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void WTSFreeMemory(IntPtr pMemory);

        // --- List view (LogViewerForm: select all rows of the virtual list in one message) ---

        internal const int LVM_SETITEMSTATE = 0x1000 + 43;
        internal const uint LVIF_STATE = 0x0008;
        internal const uint LVIS_SELECTED = 0x0002;

        // LVITEMW from CommCtrl.h. LVM_SETITEMSTATE reads only state and stateMask.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LVITEMW
        {
            public uint mask;
            public int iItem;
            public int iSubItem;
            public uint state;
            public uint stateMask;
            public IntPtr pszText;
            public int cchTextMax;
            public int iImage;
            public IntPtr lParam;
            public int iIndent;
            public int iGroupId;
            public uint cColumns;
            public IntPtr puColumns;
            public IntPtr piColFmt;
            public int iGroup;
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref LVITEMW lParam);
    }
}
