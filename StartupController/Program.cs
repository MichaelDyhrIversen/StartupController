using Microsoft.Win32;

namespace StartupController
{
    internal static class Program
    {
        // Unique mutex name for your application
        private const string MutexName = "StartupControllerSingletonMutex";

        [STAThread]
        static void Main(string[] args)
        {
            // Nothing may resolve against the directory the app was started from (e.g. Downloads with a planted
            // helper.exe): the shell searches the current directory before PATH for bare names
            try
            {
                Directory.SetCurrentDirectory(Environment.SystemDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogError("Could not set the current directory to the system directory", ex);
            }

            Mutex mutex;
            bool isNewInstance;
            try
            {
                mutex = new Mutex(true, MutexName, out isNewInstance);
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                // The name is taken by another kind of object or one we can't open: exit instead of crashing
                LoggingService.LogError("Single-instance mutex is unavailable; exiting", ex);
                return;
            }

            using (mutex)
            {
                if (isNewInstance)
                {
                    // The header comes first, so even an early exit below is logged under its own session
                    LoggingService.StartSession(args);

                    // Decided while holding the mutex and before the activation event or any UI exist (4.D8), so a
                    // relaunch loop child either meets the mutex or the recorded logon session
                    var settings = new UserSettings(Registry.CurrentUser);
                    var action = DecideStartup(args.Contains("--launch"), settings);
                    if (action == StartupAction.ExitAlreadyLaunched)
                        return; // no form, no tray icon, no signal to anyone (DecideStartup logged why)

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    // Listen for a second instance before the form exists; early requests wait in the relay
                    var relay = new ActivationRelay();
                    var activation = CreateActivation(relay);
                    try
                    {
                        // Composes the real services around the settings read above. Startup visibility (tray,
                        // --launch) is decided in Form1.SetVisibleCore. Launch mode comes from the decision only:
                        // the form never reads "Launch programs on startup" again.
                        var form = new Form1(settings) { StartupAction = action };

                        relay.Attach(form.RequestRestore);
                        Application.Run(form);
                    }
                    finally
                    {
                        activation?.Dispose();
                    }
                }
                else if (args.Contains("--launch"))
                {
                    // Started from the Run key while the app is already open: don't launch twice or pop up the window
                    LoggingService.LogInfo("Second instance with --launch; exiting without launching");
                }
                else if (!InstanceActivation.SignalExisting(InstanceActivation.DefaultEventName))
                {
                    LoggingService.LogWarning("Second instance could not signal the running instance");
                }
            }
        }

        // --launch with "Launch programs on startup" on claims the logon session (at most one --launch sequence per
        // session, 4.D8)
        private static StartupAction DecideStartup(bool hasLaunchArg, IUserSettings settings) =>
            DecideStartup(hasLaunchArg, settings, () =>
                new LaunchSessionGuard(new WtsLogonSessionKeyProvider(), new RegistryLaunchSessionStore(Registry.CurrentUser)).TryClaim());

        internal const string AlreadyLaunchedMessage =
            "--launch: startup programs were already launched in this logon session; exiting";

        internal const string BlockedMessage = "--launch blocked: nothing launched";

        // Seam for tests: the claim (real WTS and real HKCU in production) is passed in. Logs the outcome of a
        // --launch start once, after the per-step errors. An unreadable setting fails closed like an unavailable
        // session: blocked (nothing launched, the blocked balloon, exit), never a window popping up at login.
        internal static StartupAction DecideStartup(bool hasLaunchArg, IUserSettings settings, Func<LaunchClaim> claim)
        {
            if (!hasLaunchArg) return StartupAction.Normal;

            bool launchSettingOn;
            try
            {
                launchSettingOn = settings.GetLaunchProgramsOnStartup();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("--launch: could not read \"Launch programs on startup\"", ex);
                LoggingService.LogError(BlockedMessage);
                return StartupAction.BlockedAndExit;
            }

            var action = LaunchGate.Decide(hasLaunchArg, launchSettingOn, claim);
            switch (action)
            {
                case StartupAction.Normal:
                    LoggingService.LogInfo("--launch: \"Launch programs on startup\" is off; nothing is launched");
                    break;
                case StartupAction.LaunchAndExit:
                    LoggingService.LogInfo("--launch: launching the startup programs");
                    break;
                case StartupAction.ExitAlreadyLaunched:
                    LoggingService.LogInfo(AlreadyLaunchedMessage);
                    break;
                default:
                    LoggingService.LogError(BlockedMessage);
                    break;
            }
            return action;
        }

        // The app works without activation (a second launch then just exits), so a failure is logged, not fatal
        private static InstanceActivation? CreateActivation(ActivationRelay relay)
        {
            try
            {
                return new InstanceActivation(InstanceActivation.DefaultEventName, relay.Request);
            }
            catch (ActivationEventExistsException ex)
            {
                // We hold the singleton mutex, so someone else created this event (possibly with the wrong reset mode)
                LoggingService.LogWarning("Single-instance activation is unavailable: " + ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                LoggingService.LogError("Single-instance activation is unavailable", ex);
                return null;
            }
        }
    }
}
