using System.Diagnostics;

namespace StartupController.Tests.Infrastructure
{
    /// <summary>
    /// Records start requests instead of starting processes. Never calls Process.Start.
    /// </summary>
    public sealed class FakeProcessStarter : IProcessStarter
    {
        private readonly HashSet<string> _existingFiles;

        /// <summary>A starter on which every path ending in ".exe" exists (for tests that only care what gets launched).</summary>
        public static FakeProcessStarter AllExesExist() =>
            new FakeProcessStarter { ExistsWhen = p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) };

        public FakeProcessStarter(params string[] existingFiles)
        {
            _existingFiles = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        }

        public List<ProcessStartInfo> Started { get; } = new List<ProcessStartInfo>();

        public List<string> FileExistsCalls { get; } = new List<string>();

        /// <summary>Optional rule for paths that exist besides the listed ones (e.g. every *.exe path).</summary>
        public Func<string, bool>? ExistsWhen { get; set; }

        /// <summary>Every handle returned by Start, in order.</summary>
        public List<FakeHandle> Handles { get; } = new List<FakeHandle>();

        /// <summary>When set, Start records the request and then throws this exception.</summary>
        public Exception? ThrowOnStart { get; set; }

        public IDisposable? Start(ProcessStartInfo psi)
        {
            Started.Add(psi);
            if (ThrowOnStart != null) throw ThrowOnStart;
            var handle = new FakeHandle();
            Handles.Add(handle);
            return handle;
        }

        public bool FileExists(string path)
        {
            FileExistsCalls.Add(path);
            return _existingFiles.Contains(path) || (ExistsWhen?.Invoke(path) ?? false);
        }

        public sealed class FakeHandle : IDisposable
        {
            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}
