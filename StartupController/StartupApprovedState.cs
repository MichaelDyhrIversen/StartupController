namespace StartupController
{
    // Interprets and builds the binary values under Explorer\StartupApproved\Run.
    // Linked into StartupController.ReturnToWindows.exe (net462), so keep it to APIs .NET Framework 4.6.2 has.
    internal static class StartupApprovedState
    {
        // Task Manager's format: byte 0 = state, bytes 1-3 = 0, bytes 4-11 = FILETIME (little-endian)
        internal const int ValueLength = 12;

        // Check if a StartupApproved value means "Windows runs this entry"
        internal static bool IsEnabled(byte[]? value)
        {
            // No value: Windows runs the Run entry (Task Manager shows it as Enabled)
            if (value == null || value.Length == 0)
                return true;

            // Even first byte = enabled (0x02, 0x06, all-zero), odd = disabled (0x01, 0x03, 0x07)
            return (value[0] & 0x01) == 0;
        }

        // The value the takeover writes: 03 00 00 00 + FILETIME of utc, as Task Manager writes when you disable an entry.
        // utc should be UTC: a Local time is converted first, an Unspecified one is taken as UTC.
        // Throws ArgumentOutOfRangeException for a time before 1601 (no FILETIME exists for it); the caller logs it.
        internal static byte[] Disabled(DateTime utc)
        {
            var value = new byte[ValueLength];
            value[0] = 0x03;
            var normalized = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
            var fileTime = BitConverter.GetBytes(normalized.ToFileTimeUtc());
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(fileTime);
            Array.Copy(fileTime, 0, value, 4, fileTime.Length);
            return value;
        }

        // The value the uninstall helper writes to give an entry back to Windows: 02 + 11 zero bytes
        internal static byte[] Enabled()
        {
            var value = new byte[ValueLength];
            value[0] = 0x02;
            return value;
        }
    }
}
