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
        void AddThisApplicationToStartup(string exePath);
        void RemoveThisApplicationFromStartup();
    }
}
