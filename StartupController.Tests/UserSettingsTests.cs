using Microsoft.Win32;
using StartupController.Tests.Infrastructure;

namespace StartupController.Tests
{
    public sealed class UserSettingsTests : IDisposable
    {
        private readonly RegistrySandbox _sandbox = new RegistrySandbox();

        public void Dispose() => _sandbox.Dispose();

        public static IEnumerable<object[]> Settings() => new[]
        {
            new object[] { "SilenceNotifications" },
            new object[] { "StartToTray" },
            new object[] { "LaunchProgramsOnStartup" },
            new object[] { "AutoSaveOnChange" },
        };

        private static bool Get(IUserSettings s, string name) => name switch
        {
            "SilenceNotifications" => s.GetSilenceNotifications(),
            "StartToTray" => s.GetStartToTray(),
            "LaunchProgramsOnStartup" => s.GetLaunchProgramsOnStartup(),
            "AutoSaveOnChange" => s.GetAutoSaveOnChange(),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static void Set(IUserSettings s, string name, bool value)
        {
            switch (name)
            {
                case "SilenceNotifications": s.SetSilenceNotifications(value); break;
                case "StartToTray": s.SetStartToTray(value); break;
                case "LaunchProgramsOnStartup": s.SetLaunchProgramsOnStartup(value); break;
                case "AutoSaveOnChange": s.SetAutoSaveOnChange(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(name));
            }
        }

        [Theory]
        [MemberData(nameof(Settings))]
        public void Default_IsFalse(string name)
        {
            Assert.False(Get(new UserSettings(_sandbox.Root), name));
        }

        [Theory]
        [MemberData(nameof(Settings))]
        public void RoundTrip_PersistsAsDword(string name)
        {
            Set(new UserSettings(_sandbox.Root), name, true);

            Assert.Equal(1, _sandbox.ReadValue(RegistrySandbox.AppPath, name));
            Assert.Equal(RegistryValueKind.DWord, _sandbox.ReadKind(RegistrySandbox.AppPath, name));
            Assert.True(Get(new UserSettings(_sandbox.Root), name));

            Set(new UserSettings(_sandbox.Root), name, false);

            Assert.Equal(0, _sandbox.ReadValue(RegistrySandbox.AppPath, name));
            Assert.False(Get(new UserSettings(_sandbox.Root), name));
        }

        [Theory]
        [MemberData(nameof(Settings))]
        public void StringValue_IsFalse(string name)
        {
            _sandbox.SeedAppValue(name, "1", RegistryValueKind.String);

            Assert.False(Get(new UserSettings(_sandbox.Root), name));
        }

        [Fact]
        public void Get_IsCachedAfterFirstRead()
        {
            var settings = new UserSettings(_sandbox.Root);
            Assert.False(settings.GetStartToTray());

            _sandbox.SeedAppValue("StartToTray", 1, RegistryValueKind.DWord);

            Assert.False(settings.GetStartToTray());
            Assert.True(new UserSettings(_sandbox.Root).GetStartToTray());
        }

        [Fact]
        public void Get_AfterSet_ReturnsNewValue()
        {
            var settings = new UserSettings(_sandbox.Root);
            Assert.False(settings.GetAutoSaveOnChange());

            settings.SetAutoSaveOnChange(true);

            Assert.True(settings.GetAutoSaveOnChange());
        }
    }
}
