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
        public void ExistingExe_StartsOnce_WithExeArgsAndWorkingDirectory_WithoutTheShell()
        {
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");
            var launcher = new ProgramLauncher(starter, TestHostExe);

            var result = launcher.Launch(P("X", path: "\"C:\\Apps\\x.exe\" --minimized"));

            Assert.True(result.Success);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(@"C:\Apps\x.exe", psi.FileName);
            Assert.Equal("--minimized", psi.Arguments);
            Assert.Equal(@"C:\Apps", psi.WorkingDirectory);
            Assert.False(psi.UseShellExecute); // an existing .exe is started directly (item 2)
        }

        [Fact]
        public void MissingExeWithDirectory_IsNotFound_AndNeverShellStarted() // was MissingExe_ShellStartsTheRawCommand_Current
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter, TestHostExe);

            var result = launcher.Launch(P("X", path: @"C:\Missing\x.exe --flag"));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.Contains(@"C:\Missing\x.exe", result.Error, StringComparison.Ordinal);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void MissingBareExe_ShellStartsTheParsedExeAndArgs_NotTheRawString()
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter, TestHostExe);

            var result = launcher.Launch(P("R", path: "rundll32.exe shell32.dll,Foo"));

            Assert.True(result.Success);
            var psi = Assert.Single(starter.Started);
            Assert.Equal("rundll32.exe", psi.FileName);
            Assert.Equal("shell32.dll,Foo", psi.Arguments);
            Assert.True(psi.UseShellExecute);
        }

        [Fact]
        public void UnquotedPathWithSpaces_NothingExists_StartsNothing()
        {
            // Shell-starting the first token (C:\Program) could run C:\Program.exe
            var starter = new FakeProcessStarter();

            var result = new ProgramLauncher(starter, TestHostExe).Launch(P("X", path: @"C:\Program Files\x.exe -y"));

            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
        }

        [Fact]
        public void UnquotedPathWithSpaces_StartsTheLongestExistingPath()
        {
            var starter = new FakeProcessStarter(@"C:\Program.exe", @"C:\Program Files\x.exe");

            var result = new ProgramLauncher(starter, TestHostExe).Launch(P("X", path: @"C:\Program Files\x.exe -y"));

            Assert.True(result.Success);
            var psi = Assert.Single(starter.Started);
            Assert.Equal(@"C:\Program Files\x.exe", psi.FileName);
            Assert.Equal("-y", psi.Arguments);
        }

        [Fact]
        public void UnmatchedQuote_NothingExists_StartsNothing()
        {
            var starter = new FakeProcessStarter();

            var result = new ProgramLauncher(starter, TestHostExe).Launch(P("X", path: "\"C:\\a b.exe"));

            Assert.True(result.NotFound);
            Assert.Empty(starter.Started);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyPath_IsNotFound_AndStartsNothing(string path)
        {
            var starter = new FakeProcessStarter();
            var launcher = new ProgramLauncher(starter, TestHostExe);

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
            var launcher = new ProgramLauncher(starter, TestHostExe);

            var result = launcher.Launch(P("X", path: @"C:\Apps\x.exe"));

            Assert.False(result.Success);
            Assert.False(result.NotFound);
            Assert.Equal("boom", result.Error);
            Assert.Single(starter.Started);
        }

        [Fact]
        public void StarterThrowsFileNotFound_ReturnsNotFound()
        {
            // The file existed when checked but is gone when started
            var starter = new FakeProcessStarter(@"C:\Missing\x.exe") { ThrowOnStart = new FileNotFoundException("gone") };
            var launcher = new ProgramLauncher(starter, TestHostExe);

            var result = launcher.Launch(P("X", path: @"C:\Missing\x.exe"));

            Assert.False(result.Success);
            Assert.True(result.NotFound);
            Assert.Equal("gone", result.Error);
        }

        [Fact]
        public void ReturnedHandle_IsDisposed() // was ReturnedHandle_IsNotDisposed_Current (#9)
        {
            var starter = new FakeProcessStarter(@"C:\Apps\x.exe");
            FakeProcessStarter.FakeHandle? handle = null;
            var launcher = new ProgramLauncher(new CapturingStarter(starter, h => handle = h), TestHostExe);

            launcher.Launch(P("X", path: @"C:\Apps\x.exe"));

            Assert.NotNull(handle);
            Assert.True(handle!.Disposed);
        }

        [Fact]
        public void StarterReturnsNull_IsStillSuccess()
        {
            var launcher = new ProgramLauncher(new NullStarter(), TestHostExe);

            Assert.True(launcher.Launch(P("X", path: @"C:\Apps\x.exe")).Success);
        }

        private sealed class NullStarter : IProcessStarter
        {
            public IDisposable? Start(System.Diagnostics.ProcessStartInfo psi) => null;

            public bool FileExists(string path) => true;
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
