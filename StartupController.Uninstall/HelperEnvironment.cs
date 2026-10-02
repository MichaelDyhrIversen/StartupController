using System.Diagnostics;
using System.Security.Principal;

namespace StartupController.Uninstall
{
    // Who and where the helper runs. A seam so tests decide (never the test host's own token or session).
    internal interface IHelperEnvironment
    {
        // SID of the token the helper runs under (the uninstalling user when the custom action impersonates)
        string UserSid { get; }

        // LocalSystem: the custom action did not impersonate (an unpatched MSI), so HKCU is not a user's hive
        bool IsSystem { get; }

        // Elevated (administrator) token
        bool IsElevated { get; }

        // Terminal Services session; 0 is the services session, where no one can see a dialog
        int SessionId { get; }

        bool UserInteractive { get; }
    }

    internal sealed class HelperEnvironment : IHelperEnvironment
    {
        internal HelperEnvironment()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    UserSid = identity.User?.Value ?? "<unknown>";
                    IsSystem = identity.IsSystem;
                    IsElevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                // Unknown token: treat as SYSTEM and elevated, the cautious answers (no prompt, guarded log)
                UserSid = "<unknown>";
                IsSystem = true;
                IsElevated = true;
            }

            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    SessionId = process.SessionId;
                }
            }
            catch
            {
                SessionId = 0;
            }

            UserInteractive = Environment.UserInteractive;
        }

        public string UserSid { get; }
        public bool IsSystem { get; }
        public bool IsElevated { get; }
        public int SessionId { get; }
        public bool UserInteractive { get; }
    }
}
