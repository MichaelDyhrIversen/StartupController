using System.Diagnostics;

namespace StartupController
{
    // Seam over process creation and file checks so launching can be tested without starting anything.
    public interface IProcessStarter
    {
        IDisposable? Start(ProcessStartInfo psi);
        bool FileExists(string path);
    }

    public sealed class ProcessStarter : IProcessStarter
    {
        public IDisposable? Start(ProcessStartInfo psi) => Process.Start(psi);

        public bool FileExists(string path) => File.Exists(path);
    }
}
