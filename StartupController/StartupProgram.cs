public class StartupProgram
{
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required bool Enabled { get; set; }
    public required string Description { get; set; }

    // RunFingerprint of the raw Run value data when the list was loaded; empty if unknown
    public string Fingerprint { get; set; } = "";

    // True when Path already had environment variables expanded (REG_EXPAND_SZ), so the launcher doesn't expand twice
    public bool PathExpanded { get; set; }

    // Stored as enabled, but the Run data no longer matches the approved fingerprint (or has none) (D7).
    // Enabled is false while Changed is true, so it is never launched. Enable/Disable clear it.
    public bool Changed { get; set; }

    // Taken over from Windows in this load (StartupRegistryService.TakeOverWindowsEntries). Transient, never stored:
    // --launch doesn't launch it this logon because Windows has already started it (StartupSession.LaunchableAtLogon).
    public bool TakenOver { get; set; }
}
