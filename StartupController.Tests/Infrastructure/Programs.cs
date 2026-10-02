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

        public static List<StartupProgram> List(params string[] names) => names.Select(n => P(n)).ToList();

        public static byte[] Approved(byte first, int length = 12)
        {
            var value = new byte[length];
            if (length > 0) value[0] = first;
            return value;
        }
    }
}
