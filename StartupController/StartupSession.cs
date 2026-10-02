namespace StartupController
{
    // UI-free parts of loading the list and of --launch mode, so they can be tested without a form
    internal static class StartupSession
    {
        // Long enough for the last balloon to be seen before Application.Exit disposes the tray icon
        internal static readonly TimeSpan NotificationExitDelay = TimeSpan.FromSeconds(4);

        // How long --launch mode waits before exiting. A failed load always waits so its error balloon can show;
        // after launching it waits only when notifications are on.
        internal static TimeSpan ExitDelay(bool notificationsSilenced, bool loadFailed)
        {
            return loadFailed || !notificationsSilenced ? NotificationExitDelay : TimeSpan.Zero;
        }

        internal const string LaunchBlockedNotification =
            "Startup programs were not launched automatically. Open StartupController and use Launch.";

        // --launch mode after the list load: launch the enabled programs unless the start was blocked (4.D8: the
        // logon session or the launch setting couldn't be checked) or the load failed, then wait so the last balloon
        // can show. The caller exits the app afterwards.
        internal static async Task RunLaunchModeAsync(LaunchRunner runner, IReadOnlyList<StartupProgram> enabledPrograms,
            bool loaded, bool blocked, bool notificationsSilenced, INotifier notifier, Func<TimeSpan, Task>? delay = null)
        {
            delay ??= Task.Delay;
            if (blocked)
            {
                // Program.DecideStartup already logged the Error ("--launch blocked: nothing launched") and its cause.
                // This is the only balloon of a blocked start: the load doesn't notify its own failure then.
                notifier.SafeNotify(LaunchBlockedNotification);
            }
            else if (loaded)
            {
                await runner.LaunchSequenceAsync(enabledPrograms);
            }
            else
            {
                LoggingService.LogError("--launch: nothing launched because the startup list could not be loaded");
            }

            // A blocked start waits like a failed load, so its balloon shows even with notifications silenced
            var wait = ExitDelay(notificationsSilenced, loadFailed: blocked || !loaded);
            if (wait > TimeSpan.Zero)
                await delay(wait);
            LoggingService.LogInfo("--launch finished, exiting");
        }

        // Reads the list in the background and loads it into the model on the caller's (UI) thread.
        // Returns false after logging and notifying the failure; it never throws. notifyFailure is false for a blocked
        // --launch start, whose blocked balloon is the only one shown (the failure is still logged).
        internal static async Task<bool> LoadProgramsAsync(IStartupRegistry registry, StartupListModel model, INotifier notifier,
            Func<Func<List<StartupProgram>>, Task<List<StartupProgram>>>? runInBackground = null, bool notifyFailure = true)
        {
            runInBackground ??= read => Task.Run(read);
            try
            {
                var programs = await runInBackground(registry.GetStartupPrograms);
                model.Load(programs);
                LoggingService.LogInfo($"Loaded {model.Count} startup programs");
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Failed to load startup programs from HKCU\\{AppRegistryPaths.RunKey}", ex);
                if (notifyFailure)
                    notifier.SafeNotify($"Failed to load startup programs: {ex.Message}");
                return false;
            }
        }
    }
}
