namespace StartupController
{
    // Shows a modal message the user must acknowledge (a MessageBox in the app)
    public interface IMessageDialog
    {
        void ShowWarning(string text, string caption);
    }

    // Result of a --launch sequence. Skipped = blocked entries (they start StartupController itself).
    public sealed record LaunchSummary(int Launched, int Total, int Skipped, int Failed)
    {
        public string Text => $"Launched {Launched} of {Total}";
    }

    // The one place that turns launch results into log lines, notifications and dialogs, for both the manual
    // Launch button and --launch mode. Launching runs in the background; logging and notifying happen after it,
    // outside the launch try, and a failing notification never changes the logged result.
    public sealed class LaunchRunner
    {
        internal const string BlockedMessage = "This entry starts StartupController itself and can't be launched from here.";

        // --launch only: a launch that hasn't returned by then is logged as failed and the sequence moves on, so
        // --launch always exits. Manual launches have no timeout (a UAC prompt may stay open for a long time).
        internal static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(30);

        private readonly IProgramLauncher _launcher;
        private readonly INotifier _notifier;
        private readonly IMessageDialog _dialog;
        private readonly Func<Func<LaunchResult>, Task<LaunchResult>> _runInBackground;
        private readonly TimeSpan _launchTimeout;

        public LaunchRunner(IProgramLauncher launcher, INotifier notifier, IMessageDialog dialog,
            Func<Func<LaunchResult>, Task<LaunchResult>>? runInBackground = null, TimeSpan? launchTimeout = null)
        {
            _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
            _notifier = notifier ?? throw new ArgumentNullException(nameof(notifier));
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _runInBackground = runInBackground ?? (launch => Task.Run(launch));
            _launchTimeout = launchTimeout ?? DefaultLaunchTimeout;
        }

        // Manual Launch button
        public async Task<LaunchResult> LaunchManualAsync(StartupProgram program)
        {
            LoggingService.LogInfo($"Launch requested: {program.Name}");
            // No timeout: the caller keeps the Launch button disabled until the launch has really finished
            var result = await RunAsync(program, timeout: null, atLogon: false);

            if (result.Blocked)
                ShowDialog(BlockedMessage);
            else if (result.Success)
                _notifier.SafeNotify("Launched: " + program.Name);
            else
                _notifier.SafeNotify(FailureMessage(program, result));
            return result;
        }

        // --launch mode: every program in order; one failure doesn't stop the rest. Blocked entries are skipped
        // without a balloon and are not counted as launched.
        public async Task<LaunchSummary> LaunchSequenceAsync(IReadOnlyList<StartupProgram> programs)
        {
            int total = programs.Count, launched = 0, skipped = 0, failed = 0;
            for (int i = 0; i < total; i++)
            {
                var program = programs[i];
                var result = await RunAsync(program, _launchTimeout, atLogon: true);

                if (result.Blocked)
                {
                    skipped++;
                }
                else if (result.Success)
                {
                    launched++;
                    _notifier.SafeNotify($"Starting {program.Name} ({i + 1} of {total})");
                }
                else
                {
                    failed++;
                    _notifier.SafeNotify(FailureMessage(program, result));
                }
            }

            var summary = new LaunchSummary(launched, total, skipped, failed);
            LoggingService.LogInfo($"{summary.Text} (skipped {skipped}, failed {failed})");
            return summary;
        }

        internal static string FailureMessage(StartupProgram program, LaunchResult result) =>
            result.NotFound
                ? $"Executable not found for {program.Name}: {result.Error}"
                : $"Failed to launch {program.Name}: {result.Error}";

        // Launch (with an optional timeout) plus the per-entry log line: name and parsed exe, never the arguments.
        // Blocked is already logged by the launcher (name only) and gets no LAUNCH line. A timeout has no exe.
        // atLogon: the --launch sequence, which never shows a UAC prompt (IProgramLauncher.LaunchAtLogon)
        private async Task<LaunchResult> RunAsync(StartupProgram program, TimeSpan? timeout, bool atLogon)
        {
            LaunchResult result;
            try
            {
                var launch = _runInBackground(() => atLogon ? _launcher.LaunchAtLogon(program) : _launcher.Launch(program));
                if (timeout == null)
                {
                    result = await launch;
                }
                else
                {
                    using var timer = new CancellationTokenSource();
                    var finished = await Task.WhenAny(launch, Task.Delay(timeout.Value, timer.Token));
                    timer.Cancel(); // stop the timer when the launch returned first
                    if (finished == launch)
                    {
                        result = await launch;
                    }
                    else
                    {
                        LogLateCompletion(program.Name, launch);
                        result = new LaunchResult(false, Error: $"Launch did not return within {timeout.Value.TotalSeconds:0} s");
                    }
                }
            }
            catch (Exception ex)
            {
                result = new LaunchResult(false, Error: ex.Message);
            }

            if (!result.Blocked)
                LoggingService.LogLaunchResult(program.Name, result.ExePath, result.Success, result.Error ?? "");
            return result;
        }

        // A timed-out launch may still finish (e.g. after a UAC prompt); log how it ended, by name only, so the log
        // doesn't just say "failed" for a program that did start. Also observes a late exception.
        private static void LogLateCompletion(string name, Task<LaunchResult> launch)
        {
            _ = launch.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    var late = t.Result;
                    var outcome = late.Success ? "started" : late.Blocked ? "was blocked" : "failed";
                    LoggingService.LogWarning($"'{name}' finished after the launch timeout: {outcome}");
                }
                else
                {
                    LoggingService.LogWarning($"'{name}' finished after the launch timeout: failed ({t.Exception?.GetBaseException().GetType().Name ?? "cancelled"})");
                }
            }, TaskScheduler.Default);
        }

        private void ShowDialog(string message)
        {
            try
            {
                _dialog.ShowWarning(message, "Startup Controller");
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to show message", ex);
            }
        }
    }
}
