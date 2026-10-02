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
        // In the same background task, the silent takeover runs first (takeOver is the "Launch programs on startup"
        // setting, see TakeoverRequested), so the entries it takes over are listed Enabled and marked TakenOver.
        // A failed takeover is logged and never fails the load.
        // Returns false after logging and notifying the failure; it never throws. notifyFailure is false for a blocked
        // --launch start, whose blocked balloon is the only one shown (the failure is still logged).
        internal static async Task<bool> LoadProgramsAsync(IStartupRegistry registry, StartupListModel model, INotifier notifier,
            bool takeOver, Func<Func<List<StartupProgram>>, Task<List<StartupProgram>>>? runInBackground = null, bool notifyFailure = true)
        {
            runInBackground ??= read => Task.Run(read);
            try
            {
                var programs = await runInBackground(() => TakeOverAndList(registry, takeOver));
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

        private static List<StartupProgram> TakeOverAndList(IStartupRegistry registry, bool takeOver)
        {
            IReadOnlySet<string> takenOver;
            try
            {
                takenOver = registry.TakeOverWindowsEntries(takeOver);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Takeover of Windows startup entries failed; Windows keeps starting them", ex);
                takenOver = new HashSet<string>();
            }

            var programs = registry.GetStartupPrograms();
            if (takenOver.Count > 0)
            {
                var names = new HashSet<string>(takenOver, StringComparer.OrdinalIgnoreCase);
                foreach (var program in programs)
                    program.TakenOver = names.Contains(program.Name);
            }
            return programs;
        }

        // The takeover gate's setting part (D-T1): "Launch programs on startup". A setting that can't be read gives
        // false (logged), so nothing is taken over.
        internal static bool TakeoverRequested(IUserSettings settings)
        {
            try
            {
                return settings.GetLaunchProgramsOnStartup();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not read the \"Launch programs on startup\" setting; nothing is taken over from Windows", ex);
                return false;
            }
        }

        // What --launch hands to the launcher: the enabled programs except the ones taken over in this load.
        // Timing assumption: Explorer processes the Run key in one pass at logon, and StartupController's own entry is
        // one of them, so by the time this load has taken an entry over Explorer has already started (or is starting)
        // it. An entry taken over during --launch is therefore NOT started by the app in that logon; the app starts it
        // from the next logon. If the app wins the race against Explorer, that entry misses one logon (never a
        // double start); the takeover log line says "from the next logon".
        internal static IReadOnlyList<StartupProgram> LaunchableAtLogon(IEnumerable<StartupProgram> enabledPrograms)
        {
            return enabledPrograms.Where(p => !p.TakenOver).ToList();
        }

        // D-T6 text: taken-over programs start nowhere while StartupController's own Run entry is missing or disabled
        internal static string StrandedWarning(int count) =>
            $"{count} program(s) taken over by StartupController will not start at logon: StartupController's own startup " +
            "entry is missing or disabled. Re-enable StartupController in Task Manager > Startup apps (or check \"Launch " +
            "Enabled Programs On System Startup\"), or enable those programs in Task Manager > Startup apps.";

        // D-T6: the warning to show after a UI load, or null. Runs the registry read in the background; never throws
        // (a failed check is logged and shows nothing). The Warning log line is written by CountStrandedTakenOver.
        internal static async Task<string?> CheckStrandedTakeoverAsync(IStartupRegistry registry, Func<Func<int>, Task<int>>? runInBackground = null)
        {
            runInBackground ??= read => Task.Run(read);
            try
            {
                int count = await runInBackground(registry.CountStrandedTakenOver);
                return count > 0 ? StrandedWarning(count) : null;
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Could not check whether taken-over programs still start at logon", ex);
                return null;
            }
        }
    }
}
