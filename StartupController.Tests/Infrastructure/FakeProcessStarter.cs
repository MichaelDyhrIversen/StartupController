using System.Diagnostics;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>
    /// Records start requests instead of starting processes. Never calls Process.Start.
    /// </summary>
    public sealed class FakeProcessStarter : IProcessStarter
    {
        private readonly HashSet<string> _existingFiles;

        public FakeProcessStarter(params string[] existingFiles)
        {
            _existingFiles = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        }

        public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();

        public List<string> FileExistsCalls { get; } = new List<string>();

        /// <summary>When set, Start records the request and then throws this exception.</summary>
        public Exception? ThrowOnStart { get; set; }

        public IDisposable? Start(ProcessStartInfo psi)
        {
            Started.Add(psi);
            if (ThrowOnStart != null) throw ThrowOnStart;
            return new FakeHandle();
        }

        public bool FileExists(string path)
        {
            FileExistsCalls.Add(path);
            return _existingFiles.Contains(path);
        }

        public sealed class FakeHandle : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}
