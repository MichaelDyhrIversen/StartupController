namespace StartupController
{
    // What Form1 needs from the startup registry. StartupRegistryService is the real implementation;
    // tests and later phases can substitute their own.
    public interface IStartupRegistry
    {
        List<StartupProgram> GetStartupPrograms();
        void SaveStartupOrder(List<string> orderedNames);
        List<string> LoadStartupOrder();
        void AddThisApplicationToStartup(string exePath);
        void RemoveThisApplicationFromStartup();
    }
}
