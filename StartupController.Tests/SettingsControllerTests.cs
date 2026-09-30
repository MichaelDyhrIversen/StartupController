using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    // 3.3: settings checkbox logic. A failed write is logged and notified and the checkbox value reverts.
    // Registry work happens only in the sandbox (read-only handles simulate failures).
    public sealed class SettingsControllerTests : IDisposable
    {
        private const string ExePath = @"C:\Program Files\StartupController\StartupController.exe";
        private const string RunValue = "StartupController";

        private readonly RegistrySandbox _sandbox = new RegistrySandbox();
        private readonly FakeNotifier _notifier = new FakeNotifier();

        public void Dispose() => _sandbox.Dispose();

        private SettingsController Controller(Microsoft.Win32.RegistryKey settingsRoot, Microsoft.Win32.RegistryKey registryRoot) =>
            new SettingsController(new UserSettings(settingsRoot), new StartupRegistryService(registryRoot), _notifier, ExePath);

        private object? AppValue(string name) => _sandbox.ReadValue(RegistrySandbox.AppPath, name);

        [Fact]
        public void Success_ReturnsTheRequestedValue_AndSaves()
        {
            var controller = Controller(_sandbox.Root, _sandbox.Root);

            Assert.True(controller.ApplyStartToTray(true));
            Assert.True(controller.ApplySilenceNotifications(true));
            Assert.True(controller.ApplyAutoSaveOnChange(true));

            Assert.Equal(1, AppValue("StartToTray"));
            Assert.Equal(1, AppValue("SilenceNotifications"));
            Assert.Equal(1, AppValue("AutoSaveOnChange"));
            Assert.Empty(_notifier.Messages);
        }

        [Fact]
        public void ReadOnlySettingsRoot_SetThrows_AndTheValueReverts()
        {
            using var readOnly = _sandbox.OpenReadOnlyRoot();
            var settings = new UserSettings(readOnly);
            Assert.ThrowsAny<Exception>(() => settings.SetStartToTray(true));

            var controller = Controller(readOnly, readOnly);

            Assert.False(controller.ApplyStartToTray(true));
            Assert.True(controller.ApplySilenceNotifications(false));   // reverted to "on": the previous value
            Assert.False(controller.ApplyAutoSaveOnChange(true));
            Assert.Equal(3, _notifier.Messages.Count);
            Assert.All(_notifier.Messages, m => Assert.StartsWith("Failed to change", m));
            Assert.False(_sandbox.KeyExists(RegistrySandbox.AppPath));
        }

        [Fact]
        public void LaunchOnStartup_On_WritesRunEntryAndSetting_CreatingTheRunKey()
        {
            Assert.False(_sandbox.KeyExists(RegistrySandbox.RunPath));

            Assert.True(Controller(_sandbox.Root, _sandbox.Root).ApplyLaunchProgramsOnStartup(true));

            Assert.Equal("\"" + ExePath + "\" --launch", _sandbox.ReadValue(RegistrySandbox.RunPath, RunValue));
            Assert.Equal(1, AppValue("LaunchProgramsOnStartup"));
        }

        [Fact]
        public void LaunchOnStartup_Off_RemovesRunEntry()
        {
            var controller = Controller(_sandbox.Root, _sandbox.Root);
            controller.ApplyLaunchProgramsOnStartup(true);

            Assert.False(controller.ApplyLaunchProgramsOnStartup(false));

            Assert.Null(_sandbox.ReadValue(RegistrySandbox.RunPath, RunValue));
            Assert.Equal(0, AppValue("LaunchProgramsOnStartup"));
        }

        [Fact]
        public void LaunchOnStartup_RunWriteFails_SettingIsNotWritten_AndReverts()
        {
            using var readOnly = _sandbox.OpenReadOnlyRoot();

            Assert.False(Controller(_sandbox.Root, readOnly).ApplyLaunchProgramsOnStartup(true));

            Assert.Null(AppValue("LaunchProgramsOnStartup"));
            Assert.False(_sandbox.KeyExists(RegistrySandbox.RunPath));
            Assert.Contains(RegistrySandbox.RunPath, Assert.Single(_notifier.Messages));
        }

        [Fact]
        public void LaunchOnStartup_SettingWriteFails_RunChangeIsUndone()
        {
            using var readOnly = _sandbox.OpenReadOnlyRoot();

            Assert.False(Controller(readOnly, _sandbox.Root).ApplyLaunchProgramsOnStartup(true));

            Assert.Null(_sandbox.ReadValue(RegistrySandbox.RunPath, RunValue));
            Assert.Single(_notifier.Messages);
        }
    }
}
