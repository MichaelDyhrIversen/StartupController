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
    }
}
