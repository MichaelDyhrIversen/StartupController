using System.Globalization;

namespace StartupController.Uninstall
{
    internal enum UninstallChoice
    {
        Return,
        Leave,
        Prompt
    }

    // Whether to give the taken-over entries back to Windows (D-T3). Pure apart from the Warning it logs.
    internal static class UninstallDecision
    {
        internal const string PromptCaption = "StartupController";

        // How long the question waits for an answer before the silent default (Return) applies
        internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);

        // RETURNTOWINDOWS: "1" = return without asking, "0" = leave without asking, empty = decide by UI level.
        // Empty choice: UILevel exactly 3, 4 or 5 (basic, reduced, full; Settings > Apps is expected to use 3 or 4)
        // asks; the user explicitly wants to be asked. UILevel 2 (/qn), a value with flag bits (such as /passive) and
        // a missing or unparsable UILevel return without asking, because nobody may be there to answer and the
        // entries must not silently stay disabled. The prompt's hang guards are its timeout and PromptBlocker.
        internal static UninstallChoice Decide(string? uiLevel, string? choice, IUninstallLog log)
        {
            var trimmed = (choice ?? "").Trim();
            if (trimmed == "1") return UninstallChoice.Return;
            if (trimmed == "0") return UninstallChoice.Leave;
            if (trimmed.Length > 0)
                log.Warning("RETURNTOWINDOWS is not 0 or 1; treated as empty");

            if (int.TryParse((uiLevel ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level >= 3 && level <= 5)
                return UninstallChoice.Prompt;
            return UninstallChoice.Return;
        }

        // Null when the question may be shown, otherwise the rule that skips it (the silent default applies)
        internal static string? PromptBlocker(IHelperEnvironment environment)
        {
            if (environment.IsSystem) return "running as SYSTEM";
            if (environment.SessionId == 0) return "session 0 (no interactive desktop)";
            if (!environment.UserInteractive) return "not an interactive process";
            return null;
        }

        internal static string PromptText(int count) =>
            "StartupController took over " + count.ToString(CultureInfo.InvariantCulture) + " startup program(s) from Windows. " +
            "Do you want all of them to be enabled and started by Windows again?\n\n" +
            "Yes: all taken-over programs are enabled and Windows starts them at sign-in.\n" +
            "No: they stay disabled and won't start.\n\n" +
            "Without an answer within " + ((int)PromptTimeout.TotalSeconds).ToString(CultureInfo.InvariantCulture) +
            " seconds, the programs are given back to Windows.";
    }
}
