namespace StartupController
{
    // Interprets the binary values under Explorer\StartupApproved\Run.
    internal static class StartupApprovedState
    {
        // Check if a StartupApproved value means "Windows runs this entry"
        internal static bool IsEnabled(byte[]? value)
        {
            // No value: Windows runs the Run entry (Task Manager shows it as Enabled)
            if (value == null || value.Length == 0)
                return true;

            // Even first byte = enabled (0x02, 0x06, all-zero), odd = disabled (0x01, 0x03, 0x07)
            return (value[0] & 0x01) == 0;
        }
    }
}
