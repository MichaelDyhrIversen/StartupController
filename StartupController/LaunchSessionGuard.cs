using System.Globalization;
using System.Text.RegularExpressions;

namespace StartupController
{
    // Identifies the current Windows logon session (4.D8). Throws when it can't.
    internal interface ILogonSessionKeyProvider
    {
        string GetCurrentKey();
    }

    // Where the logon that last ran --launch is recorded, one record per WTS session id, so concurrent sessions of
    // the same user (console plus RDP) never overwrite each other's record.
    internal interface ILaunchSessionStore
    {
        // The key recorded for this session id, or null for no record (or a malformed one)
        string? Read(uint sessionId);

        // Records the key under the session id it contains; never touches another session's record
        void Write(string key);
    }

    internal enum LaunchClaim
    {
        Claimed,         // first --launch in this logon session; the session is now recorded
        AlreadyLaunched, // this logon session already ran --launch
        Unavailable      // the session couldn't be identified, read or recorded (fail closed: launch nothing)
    }

    // Session key text: "v1:{session id}:{logon time}", both decimal. Only equality matters.
    internal static class LaunchSessionKey
    {
        internal const int MaxLength = 64;

        // [0-9] instead of \d (which also matches other Unicode digits); \z so a trailing newline doesn't match
        private static readonly Regex Pattern = new Regex(@"^v1:[0-9]{1,10}:[0-9]{1,20}\z", RegexOptions.CultureInvariant);

        internal static string Format(uint sessionId, long logonTime) =>
            string.Create(CultureInfo.InvariantCulture, $"v1:{sessionId}:{logonTime}");

        // The session id must also fit a DWORD: it names the registry value the key is stored in
        internal static bool IsValid(string? key) => TryGetSessionId(key, out _);

        internal static bool TryGetSessionId(string? key, out uint sessionId)
        {
            sessionId = 0;
            if (key == null || key.Length > MaxLength || !Pattern.IsMatch(key)) return false;
            var id = key.AsSpan(3, key.IndexOf(':', 3) - 3);
            return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out sessionId);
        }
    }

    // At most one --launch sequence per Windows logon session (4.D8): breaks relaunch loops the self-launch
    // guard can't see (cmd /c wrappers, .lnk files, renamed copies). Program.Main calls TryClaim while it holds
    // the singleton mutex, so check and record can't interleave with another instance.
    internal sealed class LaunchSessionGuard
    {
        private readonly ILogonSessionKeyProvider _provider;
        private readonly ILaunchSessionStore _store;

        internal LaunchSessionGuard(ILogonSessionKeyProvider provider, ILaunchSessionStore store)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        // Never throws. Every failure is Unavailable, logged as an Error.
        internal LaunchClaim TryClaim()
        {
            string key;
            try
            {
                key = _provider.GetCurrentKey();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("--launch: the Windows logon session could not be identified", ex);
                return LaunchClaim.Unavailable;
            }

            if (!LaunchSessionKey.TryGetSessionId(key, out var sessionId))
            {
                // The key text is not logged: it failed validation, so it may be anything
                LoggingService.LogError("--launch: the Windows logon session key is invalid");
                return LaunchClaim.Unavailable;
            }

            var valueName = AppRegistryPaths.LaunchSessionValueName(sessionId);
            string? stored;
            try
            {
                stored = _store.Read(sessionId);
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"--launch: could not read {valueName}", ex);
                return LaunchClaim.Unavailable;
            }

            if (string.Equals(stored, key, StringComparison.Ordinal))
                return LaunchClaim.AlreadyLaunched;

            try
            {
                _store.Write(key);
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"--launch: could not record {valueName}", ex);
                return LaunchClaim.Unavailable;
            }

            return LaunchClaim.Claimed; // Program.DecideStartup logs the outcome
        }
    }
}
