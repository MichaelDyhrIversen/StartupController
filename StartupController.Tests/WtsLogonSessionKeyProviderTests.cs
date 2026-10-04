using System.Diagnostics;
using System.Globalization;

namespace StartupController.Tests
{
    // 4.D8 (19): the only test that calls the real WTS API. Read-only: it queries this session and writes nothing.
    // Skipped in session 0 or a non-interactive process (CI agents running as a service), which has no logon time.
    public class WtsLogonSessionKeyProviderTests
    {
        [InteractiveSessionFact]
        public void RealProvider_ReturnsAStableV1Key_ForThisProcessSession()
        {
            var provider = new WtsLogonSessionKeyProvider();

            var first = provider.GetCurrentKey();
            var second = provider.GetCurrentKey();

            Assert.True(LaunchSessionKey.IsValid(first), "Not a v1 key: " + first);
            Assert.Equal(first, second);
            using var process = Process.GetCurrentProcess();
            Assert.StartsWith("v1:" + process.SessionId.ToString(CultureInfo.InvariantCulture) + ":", first, StringComparison.Ordinal);
        }
    }

    // A Fact that is skipped where an interactive logon session can't be assumed
    public sealed class InteractiveSessionFactAttribute : FactAttribute
    {
        public InteractiveSessionFactAttribute()
        {
            using var process = Process.GetCurrentProcess();
            if (process.SessionId == 0 || !Environment.UserInteractive)
                Skip = "Needs an interactive logon session (session 0 and services have no WTS logon time)";
        }
    }
}
