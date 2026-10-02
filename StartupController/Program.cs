namespace StartupController
{
    internal static class Program
    {
        // Unique mutex name for your application
        private const string MutexName = "StartupControllerSingletonMutex";

        [STAThread]
        static void Main(string[] args)
        {
            // Nothing may resolve against the directory the app was started from (e.g. Downloads with a planted
            // helper.exe): the shell searches the current directory before PATH for bare names
            try
            {
                Directory.SetCurrentDirectory(Environment.SystemDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.LogError("Could not set the current directory to the system directory", ex);
            }

            Mutex mutex;
            bool isNewInstance;
            try
            {
                mutex = new Mutex(true, MutexName, out isNewInstance);
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                // The name is taken by another kind of object or one we can't open: exit instead of crashing
                LoggingService.LogError("Single-instance mutex is unavailable; exiting", ex);
                return;
            }

            using (mutex)
            {
                if (isNewInstance)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    // Listen for a second instance before the form exists; early requests wait in the relay
                    var relay = new ActivationRelay();
                    var activation = CreateActivation(relay);
                    try
                    {
                        // The parameterless constructor is the one place the real services are composed.
                        // Startup visibility (tray, --launch) is decided in Form1.SetVisibleCore.
                        var form = new Form1
                        {
                            LaunchFromStartup = args.Contains("--launch")
                        };

                        relay.Attach(form.RequestRestore);
                        Application.Run(form);
                    }
                    finally
                    {
                        activation?.Dispose();
                    }
                }
                else if (args.Contains("--launch"))
                {
                    // Started from the Run key while the app is already open: don't launch twice or pop up the window
                    LoggingService.LogInfo("Second instance with --launch; exiting without launching");
                }
                else if (!InstanceActivation.SignalExisting(InstanceActivation.DefaultEventName))
                {
                    LoggingService.LogWarning("Second instance could not signal the running instance");
                }
            }
        }

        // The app works without activation (a second launch then just exits), so a failure is logged, not fatal
        private static InstanceActivation? CreateActivation(ActivationRelay relay)
        {
            try
            {
                return new InstanceActivation(InstanceActivation.DefaultEventName, relay.Request);
            }
            catch (ActivationEventExistsException ex)
            {
                // We hold the singleton mutex, so someone else created this event (possibly with the wrong reset mode)
                LoggingService.LogWarning("Single-instance activation is unavailable: " + ex.Message);
                return null;
            }
            catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
            {
                LoggingService.LogError("Single-instance activation is unavailable", ex);
                return null;
            }
        }
    }
}
