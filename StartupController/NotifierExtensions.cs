namespace StartupController
{
    internal static class NotifierExtensions
    {
        // Notifications are best effort: a failure to show one is logged and never changes the caller's outcome
        internal static void SafeNotify(this INotifier notifier, string message)
        {
            try
            {
                notifier.Notify(message);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to show notification", ex);
            }
        }
    }
}
