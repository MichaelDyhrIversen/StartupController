using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // 3.2: --launch exit delay and the load path, without a form
    public sealed class StartupSessionTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        private static Task<List<StartupProgram>> Inline(Func<List<StartupProgram>> read) => Task.FromResult(read());

        [Theory]
        [InlineData(false, false, 4)]
        [InlineData(true, false, 0)]
        [InlineData(false, true, 4)]
        [InlineData(true, true, 4)] // a failed load always waits, so it never exits silently
        public void ExitDelay(bool silenced, bool loadFailed, int expectedSeconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), StartupSession.ExitDelay(silenced, loadFailed));
        }

        [Fact]
        public async Task Load_RegistryRootThrows_ReturnsFalse_LogsError_AndNotifies()
        {
            var root = _sandbox.OpenReadOnlyRoot();
            root.Dispose(); // every access now throws ObjectDisposedException
            var model = new StartupListModel();
            var notifier = new FakeNotifier();

            bool loaded = await StartupSession.LoadProgramsAsync(new StartupRegistryService(root), model, notifier, takeOver: false, Inline);

            Assert.False(loaded);
            Assert.Equal(0, model.Count);
            Assert.StartsWith("Failed to load startup programs", Assert.Single(notifier.Messages), StringComparison.Ordinal);
            Assert.Contains("\tERROR\t", TestLog.Read(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task Load_NotifierThrows_StillReturnsFalse()
        {
            var root = _sandbox.OpenReadOnlyRoot();
            root.Dispose();

            bool loaded = await StartupSession.LoadProgramsAsync(new StartupRegistryService(root), new StartupListModel(), new ThrowingNotifier(), takeOver: false, Inline);

            Assert.False(loaded);
        }

        [Fact]
        public async Task Load_Success_FillsTheModel()
        {
            _sandbox.SeedRun("A", @"C:\Apps\A.exe");
            _sandbox.SeedApproved("A", Programs.Approved(0x03));
            var model = new StartupListModel();
            var notifier = new FakeNotifier();

            bool loaded = await StartupSession.LoadProgramsAsync(new StartupRegistryService(_sandbox.Root), model, notifier, takeOver: false, Inline);

            Assert.True(loaded);
            Assert.Equal("A", Assert.Single(model.Programs).Name);
            Assert.Empty(notifier.Messages);
        }

        private sealed class ThrowingNotifier : INotifier
        {
            public void Notify(string message) => throw new InvalidOperationException("tray gone");
        }
    }
}
