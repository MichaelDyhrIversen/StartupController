using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace StartupController
{
    // Session key from Remote Desktop Services: WTS session id plus WTS logon time (4.D8).
    // Sign-out/in, reboot and Fast Startup give a new logon time; RDP reconnect, lock, sleep and elevation keep it.
    internal sealed class WtsLogonSessionKeyProvider : ILogonSessionKeyProvider
    {
        public string GetCurrentKey()
        {
            if (!NativeMethods.WTSQuerySessionInformationW(NativeMethods.WTS_CURRENT_SERVER_HANDLE, NativeMethods.WTS_CURRENT_SESSION,
                    NativeMethods.WTSSessionInfo, out var buffer, out var bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            try
            {
                if (buffer == IntPtr.Zero || bytes < Marshal.SizeOf<NativeMethods.WTSINFOW>())
                    throw new InvalidOperationException("WTSSessionInfo returned too little data");

                var info = Marshal.PtrToStructure<NativeMethods.WTSINFOW>(buffer);
                using var process = Process.GetCurrentProcess();
                return KeyFrom(info.SessionId, info.LogonTime, process.SessionId);
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    NativeMethods.WTSFreeMemory(buffer);
            }
        }

        // The key is valid only for this process's own session and a real logon time (session 0 has none)
        internal static string KeyFrom(uint sessionId, long logonTime, int processSessionId)
        {
            if (processSessionId < 0 || sessionId != (uint)processSessionId)
                throw new InvalidOperationException("The WTS session id does not match the process session");
            if (logonTime <= 0)
                throw new InvalidOperationException("The WTS logon time is not set");
            return LaunchSessionKey.Format(sessionId, logonTime);
        }
    }
}
