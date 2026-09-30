using System.Runtime.CompilerServices;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>
    /// Runs once when the test assembly loads, before any test:
    /// redirects LoggingService to a per-run temp folder, so tests never write to
    /// %LOCALAPPDATA%\StartupController\logs, and sweeps registry sandboxes left behind by earlier runs.
    /// </summary>
    internal static class TestLogSetup
    {
        public static string LogDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "StartupController.Tests", "logs-" + Guid.NewGuid().ToString("N"));

#pragma warning disable CA2255 // ModuleInitializer is intended here: it must run before any test touches LoggingService
        [ModuleInitializer]
#pragma warning restore CA2255
        internal static void Initialize()
        {
            LoggingService.Initialize(LogDirectory);
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try { Directory.Delete(LogDirectory, recursive: true); } catch { /* best effort */ }
            };

            try
            {
                RegistrySandbox.SweepStale();
            }
            catch
            {
                // best effort: a failed sweep must not stop the test run
            }
        }
    }
}
