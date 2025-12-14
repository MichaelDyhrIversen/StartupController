using System;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace StartupController
{
    internal static class Program
    {
        // Unique mutex name for your application
        private const string MutexName = "StartupControllerSingletonMutex";

        // Win32 API for sending a message to bring the window to front
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;

        [STAThread]
        static async Task Main(string[] args)
        {
            using (var mutex = new Mutex(true, MutexName, out bool isNewInstance))
            {
                if (isNewInstance)
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    bool startMinimized = UserSettingsService.GetStartToTray();
                    var form = new Form1();
                    if (startMinimized)
                    {
                        form.WindowState = FormWindowState.Minimized;
                        form.ShowInTaskbar = false;
                        form.Load += (s, e) => form.Hide();
                    }
                    if(args.Contains("--launch"))
                    {
                        form.LaunchFromStartup = true;
                    }
                    Application.Run(form);

                }
                else
                {
                    // Try to bring the existing instance to the foreground
                    BringExistingInstanceToFront();
                }
            }

        }

        private static void Form_Load(object? sender, EventArgs e)
        {
            throw new NotImplementedException();
        }

        private static void BringExistingInstanceToFront()
        {
            // Find the window by title (ensure your main window title is unique)
            var processes = System.Diagnostics.Process.GetProcessesByName(
                System.Diagnostics.Process.GetCurrentProcess().ProcessName);

            foreach (var process in processes)
            {
                if (process.Id != System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    IntPtr hWnd = process.MainWindowHandle;
                    if (hWnd != IntPtr.Zero)
                    {
                        if (IsIconic(hWnd))
                            ShowWindow(hWnd, SW_RESTORE);
                        SetForegroundWindow(hWnd);
                    }
                    break;
                }
            }
        }
    }
}