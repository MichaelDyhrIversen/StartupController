namespace StartupController.Tests.Infrastructure
{
    /// <summary>Reads the per-run test log (see TestLogSetup). Tests share it, so filter by a unique marker.</summary>
    internal static class TestLog
    {
        /// <summary>The whole log, oldest first, including rotated files (startupcontroller.2.log, .1.log).</summary>
        public static string Read()
        {
            var current = LoggingService.LogFilePath;
            var text = new System.Text.StringBuilder();
            for (int i = LoggingService.KeptLogFiles - 1; i >= 1; i--)
                text.Append(ReadShared(LoggingService.RotatedPath(current, i)));
            text.Append(ReadShared(current));
            return text.ToString();
        }

        private static string ReadShared(string path)
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        /// <summary>Log lines that contain the marker.</summary>
        public static List<string> LinesContaining(string marker) =>
            Read().Split('\n').Where(l => l.Contains(marker, StringComparison.Ordinal)).Select(l => l.TrimEnd('\r')).ToList();

        public static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N").Substring(0, 8);
    }

    /// <summary>Records dialogs instead of showing them.</summary>
    public sealed class FakeDialog : IMessageDialog
    {
        public List<string> Warnings { get; } = new List<string>();

        public void ShowWarning(string text, string caption) => Warnings.Add(text);
    }
}
