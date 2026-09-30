using System.ComponentModel;
using StartupController.Tests.Infrastructure;
using static StartupController.Tests.Infrastructure.Programs;

namespace StartupController.Tests
{
    // ProgramLauncher holds the launch logic moved out of Form1. Every test uses FakeProcessStarter,
    // so nothing is ever started.
    public class ProgramLauncherTests
    {
        [Fact]
        public void ExistingExe_StartsOnce_WithExeArgsAndWorkingDirectory()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");
            var launcher = new ProgramLauncher(starter);

            var result = launcher.Launch(P("X", path: "\"C:\\Apps\\x.exe\" --minimized"));

            Assert.True(result.Success);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(@"C:\Apps\x.exe", psi.FileName);
            Assert.Equal("--minimized", psi.Arguments);
            Assert.Equal(@"C:\Apps", psi.WorkingDirectory);
            Assert.True(psi.UseShellExecute);
        }

        [Fact]
        public void MissingExe_ShellStartsTheRawCommand_Current() // 3.1 changes this to the parsed exe + args
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter);

            var result = launcher.Launch(P("X", path: @"C:\Missing\x.exe --flag"));

            Assert.True(result.Success);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(@"C:\Missing\x.exe --flag", psi.FileName);
            Assert.Equal("", psi.Arguments);
            Assert.True(psi.UseShellExecute);
            Assert.Equal(@"C:\Missing\x.exe", Assert.Single(starter.FileExistsCalls));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyPath_IsNotFound_AndStartsNothing(string path)
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter);

            var result = launcher.Launch(P("X", path: path));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.False(string.IsNullOrEmpty(result.Error));
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void StarterThrows_ReturnsFailure_NotNotFound()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe") { ThrowOnStart = new Win32Exception("boom") };
            var launcher = new ProgramLauncher(starter);

            var result = launcher.Launch(P("X", path: @"C:\Apps\x.exe"));

            Assert.False(result.Success);
            Assert.False(result.NotFound);
            Assert.Equal("boom", result.Error);
            Assert.Single(starter.Started);
        }

        [Fact]
        public void StarterThrowsFileNotFound_ReturnsNotFound()
        {
            var starter = new FakeProcessStarter { ThrowOnStart = new FileNotFoundException("gone") };
            var launcher = new ProgramLauncher(starter);

            var result = launcher.Launch(P("X", path: @"C:\Missing\x.exe"));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.Equal("gone", result.Error);
        }

        [Fact]
        public void ReturnedHandle_IsNotDisposed_Current() // pinned (#9): 3.1 disposes the Process
        {
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");
            FakeProcessStarter.FakeHandle? handle = null;
            var launcher = new ProgramLauncher(new CapturingStarter(starter, h => handle = h));

            launcher.Launch(P("X", path: @"C:\Apps\x.exe"));

            Assert.NotNull(handle);
            Assert.False(handle!.Disposed);
        }

        private sealed class CapturingStarter : IProcessStarter
        {
            private readonly FakeProcessStarter _inner;
            private readonly Action<FakeProcessStarter.FakeHandle> _capture;

            public CapturingStarter(FakeProcessStarter inner, Action<FakeProcessStarter.FakeHandle> capture)
            {
                _inner = inner;
                _capture = capture;
            }

            public IDisposable? Start(System.Diagnostics.ProcessStartInfo psi)
            {
                var handle = (FakeProcessStarter.FakeHandle)_inner.Start(psi)!;
                _capture(handle);
                return handle;
            }

            public bool FileExists(string path) => _inner.FileExists(path);
        }
    }
}
