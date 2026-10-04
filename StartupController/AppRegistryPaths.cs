namespace StartupController
{
    // Registry paths shared by the services, relative to the service root (HKCU in the app).
    // StartupApproved stays private to StartupRegistryService: nothing else may touch it.
    internal static class AppRegistryPaths
    {
        internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // App settings and the stored launch list
        internal const string AppKey = @"Software\StartupController";

        // Name of the app's own Run value
        internal const string AppRunValueName = "StartupController";

        // REG_SZ values under AppKey, one per WTS session id: "LaunchSession.{id}" holds the logon in that session that
        // last ran --launch (4.D8, see RegistryLaunchSessionStore). Ids are small and reused, so the set stays bounded.
        // A bare "LaunchSession" value (pre-release builds) is never read.
        internal const string LaunchSessionValuePrefix = "LaunchSession.";

        internal static string LaunchSessionValueName(uint sessionId) =>
            LaunchSessionValuePrefix + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
