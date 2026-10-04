namespace StartupController
{
    // Persistence of the app's launch list (order and enabled names). OrderSaveCoordinator only needs this part.
    public interface IOrderStore
    {
        StoredOrder LoadStoredOrder();

        // Writes the displayed list; stored names that aren't displayed are kept in place (see OrderMerger.MergeForSave)
        void SaveStartupOrder(StoredOrder displayed);
    }

    // What Form1 needs from the startup registry. StartupRegistryService is the real implementation;
    // tests and later phases can substitute their own.
    public interface IStartupRegistry : IOrderStore
    {
        List<StartupProgram> GetStartupPrograms();

        // Silent takeover: every HKCU Run entry Windows runs itself is disabled in Windows and stored Enabled, so the
        // app launches it from the next logon. Does nothing unless launchSettingOn is true and the app's own Run entry
        // exists and is enabled in Windows. Write order: EnabledPrograms, EnabledFingerprints, ProgramOrder,
        // TakenOverPrograms, then StartupApproved, so an entry is never disabled in Windows before the app stores it.
        // Returns the names taken over by this call (case-insensitive); nothing is written when there are none.
        IReadOnlySet<string> TakeOverWindowsEntries(bool launchSettingOn);

        // D-T6: how many taken-over programs start nowhere because the app's own Run entry is missing or disabled in
        // Windows (0 otherwise). Read-only.
        int CountStrandedTakenOver();

        void AddThisApplicationToStartup(string exePath);
        void RemoveThisApplicationFromStartup();
    }
}
