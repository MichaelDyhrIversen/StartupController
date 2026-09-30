using System.Diagnostics;
using System.IO;

namespace StartupController
{
    public interface IProgramLauncher
    {
        LaunchResult Launch(StartupProgram program);
    }

    // Outcome of a single launch attempt. NotFound mirrors the FileNotFoundException path callers handled before.
    public sealed record LaunchResult(bool Success, bool NotFound = false, string? Error = null)
    {
        public static readonly LaunchResult Ok = new(true);
    }

    public sealed class ProgramLauncher : IProgramLauncher
    {
        private readonly IProcessStarter _starter;

        public ProgramLauncher(IProcessStarter starter)
        {
            _starter = starter;
        }

        public LaunchResult Launch(StartupProgram program)
        {
            try
            {
                var (exePath, arguments) = CommandLineParser.Split(program.Path, _starter.FileExists);

                if (string.IsNullOrEmpty(exePath))
                    throw new FileNotFoundException("Executable path could not be determined from entry.");

                if (!_starter.FileExists(exePath))
                {
                    // Try to start using shell (may handle URLs or AppUserModelIDs), but log clearly
                    LoggingService.LogWarning($"Executable not found: {exePath}. Attempting shell start with original command: {program.Path}");
                    var psiShell = new ProcessStartInfo(program.Path)
                    {
                        UseShellExecute = true
                    };
                    LoggingService.LogInfo($"Shell start: Command='{program.Path}'");
                    _starter.Start(psiShell);
                }
                else
                {
                    var startInfo = new ProcessStartInfo(exePath)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath),
                        Arguments = arguments
                    };
                    LoggingService.LogInfo($"Process start: Exe='{startInfo.FileName}' Args='{startInfo.Arguments}' WorkingDir='{startInfo.WorkingDirectory}'");
                    _starter.Start(startInfo);
                }

                return LaunchResult.Ok;
            }
            catch (FileNotFoundException fnf)
            {
                return new LaunchResult(false, NotFound: true, Error: fnf.Message);
            }
            catch (Exception ex)
            {
                return new LaunchResult(false, Error: ex.Message);
            }
        }
    }
}
