using System.Linq;

namespace StartupController
{
    // Interprets the binary values under Explorer\StartupApproved\Run.
    internal static class StartupApprovedState
    {
        // Check if a StartupApproved value means "enabled"
        internal static bool IsEnabled(byte[]? value)
        {
            // If no value or empty array treat as disabled
            if (value == null || value.Length == 0)
                return false;

            // If all bytes are zero, treat as enabled
            if (value.All(b => b == 0x00))
                return true;

            // Enabled: 0x02 0x00 0x00 0x00..., Disabled: 0x03 0x00 0x00 0x00...
            return value[0] == 0x02;
        }
    }
}
