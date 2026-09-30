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

        // Reads the list in the background and loads it into the model on the caller's (UI) thread.
        // Returns false after logging and notifying the failure; it never throws.
        internal static async Task<bool> LoadProgramsAsync(IStartupRegistry registry, StartupListModel model, INotifier notifier,
            Func<Func<List<StartupProgram>>, Task<List<StartupProgram>>>? runInBackground = null)
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
                notifier.SafeNotify($"Failed to load startup programs: {ex.Message}");
                return false;
            }
        }
    }
}
