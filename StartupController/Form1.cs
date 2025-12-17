using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.IO;
using System.Windows.Forms;

namespace StartupController
{
    public partial class Form1 : Form
    {
        public bool LaunchFromStartup = false;
        public Form1()
        {
            InitializeComponent();
            btnEnable.Click += (s, e) => EnableSelectedProgram();
            btnDisable.Click += (s, e) => DisableSelectedProgram();
            btnLaunch.Click += (s, e) => LaunchSelectedProgram();
            btnMoveUp.Click += (s, e) => MoveSelectedProgram(-1);
            btnMoveDown.Click += (s, e) => MoveSelectedProgram(1);
            btnSaveOrder.Click += async (s, e) => await SaveOrderAsync();
            btnHelp.Click += (s, e) => ShowHelp();
            listViewStartup.DoubleClick += (s, e) => ToggleSelectedProgram();

            notifyIcon.DoubleClick += (s, e) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.ShowInTaskbar = true;
            };
            notifyIcon.Icon = this.Icon;
            // Ensure notify icon is visible so the app can be restored from tray
            notifyIcon.Visible = true;

            // Hide to tray when minimized if the user setting is enabled
            this.Resize += (s, e) =>
            {
                if (this.WindowState == FormWindowState.Minimized && UserSettingsService.GetStartToTray())
                {
                    this.Hide();
                    this.ShowInTaskbar = false;
                    notifyIcon.Visible = true;
                }

                // Always adjust columns on resize (unless minimized)
                if (this.WindowState != FormWindowState.Minimized)
                    AdjustListViewColumns();
            };

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => Application.Exit();
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon.ContextMenuStrip.Items.Add(exitItem);
            // Add menu item to open log file from tray menu and to the View Logs button
            var openLogsItem = new ToolStripMenuItem("Open Logs");
            openLogsItem.Click += (s, e) => LoggingService.OpenLogFile();
            notifyIcon.ContextMenuStrip.Items.Add(openLogsItem);
            btnViewLogs.Click += (s, e) => LoggingService.OpenLogFile();
#pragma warning restore CS8602 // Dereference of a possibly null reference.
            chkLaunchToTray.Checked = UserSettingsService.GetStartToTray();
            chkLaunchToTray.CheckedChanged += (s, e) =>
            {
                UserSettingsService.SetStartToTray(chkLaunchToTray.Checked);
            };
            chkSilenceNotifications.Checked = UserSettingsService.GetSilenceNotifications();
            chkSilenceNotifications.CheckedChanged += (s, e) =>
            {
                UserSettingsService.SetSilenceNotifications(chkSilenceNotifications.Checked);
            };
            chkLaunchProgramsOnStartup.Checked = UserSettingsService.GetLaunchProgramsOnStartup();
            chkLaunchProgramsOnStartup.CheckedChanged += (s, e) =>
            {
                UserSettingsService.SetLaunchProgramsOnStartup(chkLaunchProgramsOnStartup.Checked);
                StartupRegistryService registryService = new StartupRegistryService();
                if (chkLaunchProgramsOnStartup.Checked)
                {
                    string exePath = Application.ExecutablePath; // or your install path
                    registryService.AddThisApplicationToStartup(exePath);
                }
                else
                {
                    registryService.RemoveThisApplicationFromStartup();
                }
            };
            this.Load += async (s, e) =>
            {
                LoggingService.StartSession(string.Join(' ', Environment.GetCommandLineArgs()));
                LoggingService.LogInfo("Loading startup programs");
                await LoadStartupPrograms();
                if (chkLaunchProgramsOnStartup.Checked && this.LaunchFromStartup)
                {
                    await LaunchEnabledProgramsAsync();
                    Application.Exit();
                }
            };

            // initial column sizing
            AdjustListViewColumns();
        }

        private List<StartupProgram> startupPrograms = new List<StartupProgram>();

        private async Task LoadStartupPrograms()
        {
            try
            {
                // Simulate or perform actual registry access asynchronously
                startupPrograms = await Task.Run(() =>
                {
                    StartupRegistryService registryService = new StartupRegistryService();
                    return registryService.GetStartupPrograms();
                });
                //ShowStartupNotification($"Loaded {startupPrograms.Count.ToString()} startup programs.");
                RefreshListView();
                LoggingService.LogInfo($"Loaded {startupPrograms.Count} startup programs");
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to load startup programs", ex);
                ShowNotification($"Failed to load startup programs: {ex.Message}");
            }
        }

        private void RefreshListView()
        {
            listViewStartup.Items.Clear();
            foreach (var prog in startupPrograms)
            {
                var item = new ListViewItem(new[]
                {
            prog.Name,
            prog.Enabled ? "Enabled" : "Disabled",
            prog.Path,
            prog.Description
        });
                item.Tag = prog;
                listViewStartup.Items.Add(item);
            }

            AdjustListViewColumns();
        }
        /*
        private void AdjustListViewColumns()
        {
            try
            {
                // Ensure there is space calculations based on client width
                var avail = listViewStartup.ClientSize.Width;
                // Reserve widths for Name, Status and Description columns (min values)
                int nameMin = 140;
                int statusWidth = 80; // small fixed for status
                int descMin = 170;
                int padding = 8; // some padding

                int pathWidth = avail - (nameMin + statusWidth + descMin + padding);
                if (pathWidth < 100) pathWidth = 100; // minimum for path

                // Apply widths (column order: Name, Status, Path, Description)
                if (listViewStartup.Columns.Count >= 4)
                {
                    listViewStartup.BeginUpdate();
                    listViewStartup.Columns[0].Width = nameMin;
                    listViewStartup.Columns[1].Width = statusWidth;
                    listViewStartup.Columns[2].Width = pathWidth;
                    listViewStartup.Columns[3].Width = descMin;
                    listViewStartup.EndUpdate();
                }
            }
            catch
            {
                // ignore layout failures
            }
        }*/

        private void AdjustListViewColumns()
        {
            // Reserve space for fixed columns
            int nameWidth = 140;
            int statusWidth = 80;
            int descWidth = 170;

            // Calculate the available width for the Path column
            int pathWidth = listViewStartup.ClientSize.Width - nameWidth - statusWidth - descWidth - 20; // 20px for padding

            // Ensure minimum width
            if (pathWidth < 100) pathWidth = 100;

            // Begin updating the ListView columns
            listViewStartup.BeginUpdate();
            try
            {
                // Set the widths of the columns by index
                listViewStartup.Columns[0].Width = nameWidth;
                listViewStartup.Columns[1].Width = statusWidth;
                listViewStartup.Columns[2].Width = pathWidth;
                listViewStartup.Columns[3].Width = descWidth;
            }
            finally
            {
                // Ensure the ListView ends the update
                listViewStartup.EndUpdate();
            }
        }

        private async void ToggleSelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            try
            {
                var prog = listViewStartup.SelectedItems[0].Tag as StartupProgram;
                prog.Enabled = !prog.Enabled;
                // TODO: Update registry or startup folder asynchronously
                await Task.Run(() => {/* registry update logic here */});
                RefreshListView();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to toggle program", ex);
                ShowNotification($"Failed to enable program: {ex.Message}");
            }
        }
        private async void EnableSelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            try
            {
                var prog = listViewStartup.SelectedItems[0].Tag as StartupProgram;
                prog.Enabled = true;
                // TODO: Update registry or startup folder asynchronously
                await Task.Run(() => {/* registry update logic here */});
                RefreshListView();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to enable program", ex);
                ShowNotification($"Failed to enable program: {ex.Message}");
            }
        }

        private void DisableSelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            try
            {
                var prog = listViewStartup.SelectedItems[0].Tag as StartupProgram;
                prog.Enabled = false;
                // TODO: Update registry or startup folder
                RefreshListView();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to disable program", ex);
                ShowNotification($"Failed to disable program: {ex.Message}");
            }
        }

        // Helper: split a registry "run" command into executable path and arguments
        private static (string exePath, string args) SplitCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return ("", "");

            command = command.Trim();

            // If starts with a quote, take the quoted part as the exe path
            if (command.StartsWith("\""))
            {
                var endQuote = command.IndexOf('"', 1);
                if (endQuote > 0)
                {
                    var exe = command.Substring(1, endQuote - 1);
                    var args = command.Substring(endQuote + 1).Trim();
                    return (exe, args);
                }
            }

            // Try to find a common executable extension (.exe, .bat, .cmd, .com, .lnk)
            var m = Regex.Match(command, "^(.+?\\.(exe|bat|cmd|com|lnk))(\\s+.*)?$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var exe = m.Groups[1].Value;
                var args = m.Groups[3].Success ? m.Groups[3].Value.Trim() : string.Empty;
                return (exe, args);
            }

            // Fallback: split on first space
            var idx = command.IndexOf(' ');
            if (idx > 0)
            {
                var exe = command.Substring(0, idx);
                var args = command.Substring(idx + 1).Trim();
                return (exe, args);
            }

            return (command, string.Empty);
        }

        private async void LaunchSelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            var prog = listViewStartup.SelectedItems[0].Tag as StartupProgram;
            try
            {
                await Task.Run(() =>
                {
                    var (exePath, arguments) = SplitCommand(prog.Path);

                    if (string.IsNullOrEmpty(exePath))
                        throw new FileNotFoundException("Executable path could not be determined from entry.");

                    if (!File.Exists(exePath))
                    {
                        // Try to start using shell (may handle URLs or AppUserModelIDs), but log clearly
                        LoggingService.LogWarning($"Executable not found: {exePath}. Attempting shell start with original command.");
                        var psiShell = new ProcessStartInfo(prog.Path)
                        {
                            UseShellExecute = true
                        };
                        Process.Start(psiShell);
                    }
                    else
                    {
                        var startInfo = new ProcessStartInfo(exePath)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(exePath),
                            Arguments = arguments
                        };
                        Process.Start(startInfo);
                    }
                });

                ShowNotification("Launched: " + prog.Name);
                LoggingService.LogLaunchResult(prog.Name, prog.Path, true);
            }
            catch (FileNotFoundException fnf)
            {
                LoggingService.LogLaunchResult(prog.Name, prog.Path, false, fnf.Message);
                ShowNotification($"Executable not found: {fnf.Message}");
            }
            catch (Exception ex)
            {
                LoggingService.LogLaunchResult(prog.Name, prog.Path, false, ex.Message);
                ShowNotification($"Failed to launch: {ex.Message}");
            }
        }

        public async Task LaunchEnabledProgramsAsync()
        {
            if (!this.LaunchFromStartup) return;
            var enabledPrograms = startupPrograms.Where(p => p.Enabled).ToList();
            int total = enabledPrograms.Count;
            int current = 1;

            foreach (var prog in enabledPrograms)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        var (exePath, arguments) = SplitCommand(prog.Path);

                        if (string.IsNullOrEmpty(exePath))
                            throw new FileNotFoundException("Executable path could not be determined from entry.");

                        if (!File.Exists(exePath))
                        {
                            LoggingService.LogWarning($"Executable not found: {exePath}. Attempting shell start with original command.");
                            var psiShell = new ProcessStartInfo(prog.Path)
                            {
                                UseShellExecute = true
                            };
                            Process.Start(psiShell);
                        }
                        else
                        {
                            var startInfo = new ProcessStartInfo(exePath)
                            {
                                UseShellExecute = true,
                                WorkingDirectory = Path.GetDirectoryName(exePath),
                                Arguments = arguments
                            };
                            Process.Start(startInfo);
                        }
                    });

                    ShowStartupNotification(prog.Name, current, total);
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, true);
                }
                catch (FileNotFoundException fnf)
                {
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, false, fnf.Message);
                }
                catch (Exception ex)
                {
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, false, ex.Message);
                    ShowNotification($"Failed to launch {prog.Name}: {ex.Message}");
                }
                current++;
            }
        }
        private void MoveSelectedProgram(int direction)
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            try
            {
                int index = listViewStartup.SelectedItems[0].Index;
                int newIndex = index + direction;
                if (newIndex < 0 || newIndex >= startupPrograms.Count) return;
                var item = startupPrograms[index];
                startupPrograms.RemoveAt(index);
                startupPrograms.Insert(newIndex, item);
                RefreshListView();
                listViewStartup.Items[newIndex].Selected = true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to move program", ex);
                ShowNotification($"Failed to move program: {ex.Message}");
            }
        }
        private async Task SaveOrderAsync()
        {
            try
            {
                // TODO: Persist the order (e.g., to a config file)
                await Task.Run(() =>
                {
                    StartupRegistryService registryService = new StartupRegistryService();
                    registryService.SaveStartupOrder(startupPrograms.Where(p => p.Enabled).Select(p => p.Name).ToList());
                });
                ShowNotification("Order saved!");
                LoggingService.LogInfo("Order saved");
            }
            catch (Exception ex)
            {
                ShowNotification($"Failed to save order: {ex.Message}");
                LoggingService.LogError("Failed to save order", ex);
            }
        }
        private void ShowHelp()
        {
            MessageBox.Show("This application allows you to manage disabled startup programs. Select a program to enable, disable, launch, or reorder it. Use Save Order to persist your preferred startup sequence.");
        }
        public void ShowStartupNotification(string programName, int current, int total)
        {
            ShowNotification($"Starting {programName} ({current} of {total})");
        }

        private void ShowNotification(string text)
        {
            if (UserSettingsService.GetSilenceNotifications()) return; // Check your setting
            notifyIcon.BalloonTipTitle = "Startup Controller";
            notifyIcon.BalloonTipText = text;
            notifyIcon.ShowBalloonTip(3000); // Show for 3 seconds
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (UserSettingsService.GetStartToTray()) // Your setting
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }
        }
    }
}
