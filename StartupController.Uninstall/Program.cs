using System.Globalization;
using Microsoft.Win32;

namespace StartupController.Uninstall
{
    // StartupController.ReturnToWindows.exe, run by the MSI's Uninstall custom action (impersonated, so HKCU is the
    // uninstalling user's hive): --uilevel [UILevel] --choice "[RETURNTOWINDOWS]".
    // Always exits 0: a non-zero exit code could fail and roll back the uninstall.
    internal static class Program
    {
        // Longest argument value written to the log
        private const int MaxLoggedArgument = 32;

        private const string OtherProfilesNote = "other user profiles on this machine were not handled; their taken-over programs stay disabled (enable them in Task Manager > Startup apps)";

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                var environment = new HelperEnvironment();
                return Run(args, Registry.CurrentUser, new MessageBoxPrompt(), UninstallLog.For(environment, UninstallLog.DefaultPath()), environment);
            }
            catch
            {
                return 0;
            }
        }

        internal static int Run(string[] args, RegistryKey root, IPrompt prompt, IUninstallLog log, IHelperEnvironment environment)
        {
            try
            {
                string? uiLevel = Argument(args, "--uilevel");
                string? choice = Argument(args, "--choice");
                var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
                log.Info("=== StartupController uninstall helper " + version + " | user: " + environment.UserSid
                    + " | UILevel: " + Shorten(uiLevel) + " | choice: " + Shorten(choice) + " ===");
                foreach (var name in new[] { "--uilevel", "--choice" })
                {
                    if (Occurrences(args, name) > 1)
                        log.Warning(name + " is given more than once; treated as unparsable");
                }

                if (environment.IsSystem)
                    log.Warning("Running as SYSTEM: the custom action did not impersonate the uninstalling user, so no user's startup programs are handled; " + OtherProfilesNote);

                var names = ReturnToWindows.RecordedNames(root, log);
                int count = ReturnToWindows.ReturnableCount(root, names);
                if (count == 0)
                {
                    log.Warning("No taken-over programs to return for user " + environment.UserSid + "; " + OtherProfilesNote);
                    log.Info("Returned 0 of 0");
                    return 0;
                }

                bool returnThem;
                switch (UninstallDecision.Decide(uiLevel, choice, log))
                {
                    case UninstallChoice.Prompt:
                        returnThem = AskOrDefault(prompt, environment, count, log);
                        break;
                    case UninstallChoice.Leave:
                        returnThem = false;
                        log.Info("Property: Leave");
                        break;
                    default:
                        returnThem = true;
                        log.Info(IsZeroOrOne(choice) ? "Property: Return" : "Silent: Return");
                        break;
                }

                if (!returnThem)
                {
                    log.Info("Returned 0 of " + count.ToString(CultureInfo.InvariantCulture));
                    return 0;
                }

                var result = new ReturnToWindows().Execute(root, log, names);
                log.Info("Returned " + result.Returned.ToString(CultureInfo.InvariantCulture) + " of "
                    + result.Recorded.ToString(CultureInfo.InvariantCulture));
                log.Info("Only user " + environment.UserSid + " was handled; " + OtherProfilesNote);
            }
            catch (Exception ex)
            {
                try
                {
                    log.Error("Uninstall helper failed (" + ex.GetType().Name + ")");
                }
                catch
                {
                    // ignored
                }
            }
            return 0;
        }

        // Shows the question unless a rule forbids it. Only an explicit No leaves the programs disabled; no answer
        // within the timeout, a skipped question and a question that failed (or threw) mean the silent default (Return).
        private static bool AskOrDefault(IPrompt prompt, IHelperEnvironment environment, int count, IUninstallLog log)
        {
            var blocker = UninstallDecision.PromptBlocker(environment);
            if (blocker != null)
            {
                log.Info("Prompt skipped (" + blocker + "): Silent: Return");
                return true;
            }

            PromptAnswer answer;
            try
            {
                answer = prompt.Ask(UninstallDecision.PromptText(count), UninstallDecision.PromptCaption, UninstallDecision.PromptTimeout);
            }
            catch (Exception ex)
            {
                log.Warning("The question could not be shown (" + ex.GetType().Name + ")");
                answer = PromptAnswer.Failed;
            }

            switch (answer)
            {
                case PromptAnswer.Yes:
                    log.Info("Prompted: Yes");
                    return true;
                case PromptAnswer.TimedOut:
                    log.Info("Prompted: no answer within " + ((int)UninstallDecision.PromptTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s: Silent: Return");
                    return true;
                case PromptAnswer.No:
                    log.Info("Prompted: No");
                    return false;
                default:
                    log.Info("Prompt failed: silent default: Return");
                    return true;
            }
        }

        private static bool IsZeroOrOne(string? value)
        {
            var trimmed = (value ?? "").Trim();
            return trimmed == "0" || trimmed == "1";
        }

        // The value after the switch (case-insensitive). Null when the switch or its value is missing, or when the
        // switch is given more than once (unparsable: the silent default decides).
        internal static string? Argument(string[]? args, string name)
        {
            if (args == null || Occurrences(args, name) != 1) return null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static int Occurrences(string[]? args, string name)
        {
            if (args == null) return 0;
            int count = 0;
            foreach (var arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) count++;
            }
            return count;
        }

        private static string Shorten(string? value)
        {
            if (value == null) return "<none>";
            return value.Length <= MaxLoggedArgument ? value : value.Substring(0, MaxLoggedArgument) + "...";
        }
    }
}
