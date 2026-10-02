namespace StartupController
{
    // HKCU\Software\StartupController\TakenOverPrograms (REG_MULTI_SZ): the Run value names the app disabled in
    // Windows when it took them over (D-T3). Written by StartupRegistryService before StartupApproved, read and pruned
    // by the uninstall helper. Names only: no commands, paths or bytes.
    // Linked into StartupController.ReturnToWindows.exe (net462), so both sides read the same format.
    internal static class TakeoverRecord
    {
        internal const string ValueName = "TakenOverPrograms";

        // Same caps as the stored order (StartupRegistryService.MAX_NAMES / MAX_NAME_LENGTH)
        internal const int MaxNames = 1024;
        internal const int MaxNameLength = 260;

        // Missing value (null): true with no names. A value of any kind other than REG_MULTI_SZ: false (the caller
        // must not trust or overwrite it).
        internal static bool TryRead(object? value, out List<string> names)
        {
            names = new List<string>();
            if (value == null) return true;
            if (!(value is string[] lines)) return false;
            names = Clean(lines, out _);
            return true;
        }

        // Drops empty/whitespace and over-long names and case-insensitive duplicates (first one wins), then keeps
        // at most MaxNames. dropped counts what was removed.
        internal static List<string> Clean(IEnumerable<string?> names, out int dropped)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            dropped = 0;
            foreach (var name in names)
            {
                if (!IsStorable(name) || !seen.Add(name!) || result.Count >= MaxNames)
                {
                    dropped++;
                    continue;
                }
                result.Add(name!);
            }
            return result;
        }

        // A name that can be recorded: not empty or whitespace, at most MaxNameLength characters and without a NUL
        // (REG_MULTI_SZ would split it, and a lookup by name would find a different value)
        internal static bool IsStorable(string? name) =>
            !string.IsNullOrWhiteSpace(name) && name!.Length <= MaxNameLength && name.IndexOf('\0') < 0;
    }
}
