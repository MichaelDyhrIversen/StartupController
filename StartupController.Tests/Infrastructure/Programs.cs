namespace StartupController.Tests.Infrastructure
{
    internal static class Programs
    {
        /// <summary>Self path for ProgramLauncher in tests that don't care about it: the test host, as the removed one-argument constructor used.</summary>
        public static string TestHostExe => Environment.ProcessPath ?? "";

        public static StartupProgram P(string name, bool enabled = false, string? path = null) => new StartupProgram
        {
            Name = name,
            Path = path ?? $@"C:\Apps\{name}.exe",
            Enabled = enabled,
            Description = ""
        };

        /// <summary>The app path the takeover tests seed as StartupController's own Run entry ("C:\x\StartupController.exe" --launch).</summary>
        public const string TestAppExe = @"C:\x\StartupController.exe";

        /// <summary>Own-entry check for StartupRegistryService in tests: the parsed exe is TestAppExe.</summary>
        public static bool IsTestApp(string exePath) => string.Equals(exePath, TestAppExe, StringComparison.OrdinalIgnoreCase);

        public static List<StartupProgram> List(params string[] names) => names.Select(n => P(n)).ToList();

        public static byte[] Approved(byte first, int length = 12)
        {
            var value = new byte[length];
            if (length > 0) value[0] = first;
            return value;
        }
    }
}
