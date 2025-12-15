using System.Diagnostics;
using System.Net.WebSockets;
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
            };

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) => Application.Exit();
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            notifyIcon.ContextMenuStrip.Items.Add(exitItem);
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
                await LoadStartupPrograms();
                if (chkLaunchProgramsOnStartup.Checked && this.LaunchFromStartup)
                {
                    await LaunchEnabledProgramsAsync();
                    Application.Exit();
                }
            };
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
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load startup programs: {ex.Message}");
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
            prog.Path,
            prog.Enabled ? "Enabled" : "Disabled",
            prog.Description
        });
                item.Tag = prog;
                listViewStartup.Items.Add(item);
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
                MessageBox.Show($"Failed to enable program: {ex.Message}");
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
                MessageBox.Show($"Failed to enable program: {ex.Message}");
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
                MessageBox.Show($"Failed to disable program: {ex.Message}");
            }
        }

        private async void LaunchSelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return;
            var prog = listViewStartup.SelectedItems[0].Tag as StartupProgram;
            try
            {
                await Task.Run(() =>
                {
                    string arguments = "";
                    if (prog.Path.Contains("\""))
                    {
                        // lets get the path between the quotes
                        var path = prog.Path.Substring(1, prog.Path.IndexOf("\"", 2) - 1);
                        arguments = prog.Path.Substring(prog.Path.IndexOf("\"", 2) + 1).Trim();
                        // Handle paths with quotes
                        prog.Path = path;
                    }
                    var startInfo = new ProcessStartInfo(prog.Path)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(prog.Path),
                        Arguments = arguments
                    };
                    Process.Start(startInfo);
                });
                ShowStartupNotification("Launched: " + prog.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch: {ex.Message}");
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
                MessageBox.Show($"Failed to move program: {ex.Message}");
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
                ShowStartupNotification("Order saved!");
            }
            catch (Exception ex)
            {
                ShowStartupNotification($"Failed to save order: {ex.Message}");
            }
        }
        private void ShowHelp()
        {
            MessageBox.Show("This application allows you to manage disabled startup programs. Select a program to enable, disable, launch, or reorder it. Use Save Order to persist your preferred startup sequence.");
        }
        public void ShowStartupNotification(string programName, int current, int total)
        {
            ShowStartupNotification($"Starting {programName} ({current} of {total})");
        }

        private void ShowStartupNotification(string text)
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

        public async Task LaunchEnabledProgramsAsync()
        {
            if(!this.LaunchFromStartup) return; 
            var enabledPrograms = startupPrograms.Where(p => p.Enabled).ToList();
            int total = enabledPrograms.Count;
            int current = 1;

            foreach (var prog in enabledPrograms)
            {
                try
                {
                    await Task.Run(() =>
                    {
                        string arguments = "";
                        string path = prog.Path;
                        if (path.Contains("\""))
                        {
                            path = path.Substring(1, path.IndexOf("\"", 2) - 1);
                            arguments = prog.Path.Substring(prog.Path.IndexOf("\"", 2) + 1).Trim();
                        }
                        var startInfo = new ProcessStartInfo(path)
                        {
                            UseShellExecute = true,
                            WorkingDirectory = Path.GetDirectoryName(path),
                            Arguments = arguments
                        };
                        Process.Start(startInfo);
                    });
                    ShowStartupNotification(prog.Name, current, total);
                }
                catch (Exception ex)
                {
                    ShowStartupNotification($"Failed to launch {prog.Name}: {ex.Message}");
                }
                current++;
            }
        }
    }
}
