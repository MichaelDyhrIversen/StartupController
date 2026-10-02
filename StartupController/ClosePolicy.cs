namespace StartupController
{
    public enum CloseDecision
    {
        CloseSilently,
        Prompt
    }

    // Whether closing the main window asks about unsaved changes
    public static class ClosePolicy
    {
        // Windows shutdown/logoff: never prompt or save (D5), the changes are discarded.
        // Any other reason prompts when the list is dirty.
        public static CloseDecision Decide(CloseReason reason, bool isDirty)
        {
            if (!isDirty) return CloseDecision.CloseSilently;
            if (reason == CloseReason.WindowsShutDown) return CloseDecision.CloseSilently;
            return CloseDecision.Prompt;
        }
    }
}
