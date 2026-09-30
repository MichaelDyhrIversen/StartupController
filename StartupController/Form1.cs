using System.Diagnostics;
using System.Net.WebSockets;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StartupController
{
    public partial class Form1 : Form
    {
        public bool LaunchFromStartup = false;
        private readonly IUserSettings _settings;
        private readonly IStartupRegistry _registry;
        private readonly IProgramLauncher _launcher;
        private readonly StartupListModel _model = new StartupListModel();

        // Used by the WinForms designer; the app goes through Program.cs with the same defaults
        public Form1()
            : this(new UserSettings(Registry.CurrentUser), new StartupRegistryService(), new ProgramLauncher(new ProcessStarter()))
        {
        }

        public Form1(IUserSettings settings, IStartupRegistry registry, IProgramLauncher launcher)
        {
            _settings = settings;
            _registry = registry;
            _launcher = launcher;

            InitializeComponent();
            btnEnable.Click += (s, e) => EnableSelectedProgram();
            btnDisable.Click += (s, e) => DisableSelectedProgram();
            btnLaunch.Click += (s, e) => LaunchSelectedProgram();
            btnMoveUp.Click += (s, e) => MoveSelectedProgram(-1);
            btnMoveTop.Click += (s, e) => MoveSelectedProgramToTop();
            btnMoveDown.Click += (s, e) => MoveSelectedProgram(1);
            btnMoveBottom.Click += (s, e) => MoveSelectedProgramToBottom();
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
                if (this.WindowState == FormWindowState.Minimized && _settings.GetStartToTray())
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
            chkLaunchToTray.Checked = _settings.GetStartToTray();
            chkLaunchToTray.CheckedChanged += (s, e) =>
            {
                _settings.SetStartToTray(chkLaunchToTray.Checked);
            };
            chkSilenceNotifications.Checked = _settings.GetSilenceNotifications();
            chkSilenceNotifications.CheckedChanged += (s, e) =>
            {
                _settings.SetSilenceNotifications(chkSilenceNotifications.Checked);
            };
            chkLaunchProgramsOnStartup.Checked = _settings.GetLaunchProgramsOnStartup();
            chkLaunchProgramsOnStartup.CheckedChanged += (s, e) =>
            {
                _settings.SetLaunchProgramsOnStartup(chkLaunchProgramsOnStartup.Checked);
                if (chkLaunchProgramsOnStartup.Checked)
                {
                    string exePath = Application.ExecutablePath; // or your install path
                    _registry.AddThisApplicationToStartup(exePath);
                }
                else
                {
                    _registry.RemoveThisApplicationFromStartup();
                }
            };

            // autosave checkbox
            chkAutoSaveOnChange.Checked = _settings.GetAutoSaveOnChange();
            chkAutoSaveOnChange.CheckedChanged += (s, e) =>
            {
                _settings.SetAutoSaveOnChange(chkAutoSaveOnChange.Checked);
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

            // handle closing to prompt for unsaved changes
            this.FormClosing += async (s, e) =>
            {
                if (_model.IsDirty)
                {
                    var res = MessageBox.Show("There are unsaved changes. Save before exiting?", "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (res == DialogResult.Cancel)
                    {
                        e.Cancel = true;
                        return;
                    }
                    if (res == DialogResult.Yes)
                    {
                        await SaveOrderAsync();
                        // If still dirty after save, cancel closing
                        if (_model.IsDirty)
                            e.Cancel = true;
                    }
                }
            };

            // initial column sizing
            AdjustListViewColumns();
        }

        private void SetDirty(bool dirty)
        {
            // If autosave is enabled and we are marking dirty, perform immediate save instead of keeping dirty state
            try
            {
                if (dirty && _settings.GetAutoSaveOnChange())
                {
                    // trigger save in background and do not set isDirty
                    _ = SaveOrderAsync();
                    return;
                }

                if (dirty)
                    _model.MarkDirty();
                else
                    _model.MarkClean();

                // visual cue on save button unless autosave is enabled
                if (_model.IsDirty)
                {
                    btnSaveOrder.Enabled = true;
                    if (!_settings.GetAutoSaveOnChange())
                    {
                        btnSaveOrder.BackColor = System.Drawing.Color.LightSalmon;
                    }
                    else
                    {
                        btnSaveOrder.BackColor = btnDisable.BackColor;
                    }
                }
                else
                {
                    btnSaveOrder.Enabled = true; // still enabled so user can save if they want
                    btnSaveOrder.BackColor = btnDisable.BackColor;
                }
            }
            catch { }
        }

        private async Task LoadStartupPrograms()
        {
            try
            {
                // Simulate or perform actual registry access asynchronously
                var programs = await Task.Run(() => _registry.GetStartupPrograms());
                _model.Load(programs);
                //ShowStartupNotification($"Loaded {_model.Count} startup programs.");
                RefreshListView();
                LoggingService.LogInfo($"Loaded {_model.Count} startup programs");
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
            foreach (var prog in _model.Programs)
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

        // The program behind the selected row (via Tag, so it stays correct if the view is sorted)
        private StartupProgram? SelectedProgram()
        {
            if (listViewStartup.SelectedItems.Count == 0) return null;
            return listViewStartup.SelectedItems[0].Tag as StartupProgram;
        }

        // Select the row showing this program instance
        private void SelectProgram(StartupProgram program)
        {
            foreach (ListViewItem item in listViewStartup.Items)
            {
                if (ReferenceEquals(item.Tag, program))
                {
                    item.Selected = true;
                    return;
                }
            }
        }

        private async void ToggleSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                _model.Toggle(prog);
                // TODO: Update registry or startup folder asynchronously
                await Task.Run(() => {/* registry update logic here */});
                RefreshListView();
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to toggle program", ex);
                ShowNotification($"Failed to enable program: {ex.Message}");
            }
        }
        private async void EnableSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                _model.Enable(prog);
                // TODO: Update registry or startup folder asynchronously
                await Task.Run(() => {/* registry update logic here */});
                RefreshListView();
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to enable program", ex);
                ShowNotification($"Failed to enable program: {ex.Message}");
            }
        }

        private void DisableSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                _model.Disable(prog);
                // TODO: Update registry or startup folder
                RefreshListView();
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to disable program", ex);
                ShowNotification($"Failed to disable program: {ex.Message}");
            }
        }

        private async void LaunchSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                var result = await Task.Run(() => _launcher.Launch(prog));
                if (result.Success)
                {
                    ShowNotification("Launched: " + prog.Name);
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, true);
                }
                else if (result.NotFound)
                {
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, false, result.Error ?? "");
                    ShowNotification($"Executable not found: {result.Error}");
                }
                else
                {
                    LoggingService.LogLaunchResult(prog.Name, prog.Path, false, result.Error ?? "");
                    ShowNotification($"Failed to launch: {result.Error}");
                }
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
            var enabledPrograms = _model.EnabledPrograms();
            int total = enabledPrograms.Count;
            int current = 1;

            foreach (var prog in enabledPrograms)
            {
                try
                {
                    var result = await Task.Run(() => _launcher.Launch(prog));
                    if (result.Success)
                    {
                        ShowStartupNotification(prog.Name, current, total);
                        //LoggingService.LogLaunchResult(prog.Name, prog.Path, true);
                    }
                    else if (result.NotFound)
                    {
                        LoggingService.LogLaunchResult(prog.Name, prog.Path, false, result.Error ?? "");
                    }
                    else
                    {
                        LoggingService.LogLaunchResult(prog.Name, prog.Path, false, result.Error ?? "");
                        ShowNotification($"Failed to launch {prog.Name}: {result.Error}");
                    }
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
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                bool moved = direction < 0 ? _model.MoveUp(prog) : _model.MoveDown(prog);
                if (!moved) return;
                RefreshListView();
                SelectProgram(prog);
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to move program", ex);
                ShowNotification($"Failed to move program: {ex.Message}");
            }
        }
        private void MoveSelectedProgramToTop()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                if (!_model.MoveTop(prog)) return; // already at top
                RefreshListView();
                SelectProgram(prog);
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to move program to top", ex);
                ShowNotification($"Failed to move program to top: {ex.Message}");
            }
        }

        private void MoveSelectedProgramToBottom()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                if (!_model.MoveBottom(prog)) return; // already at bottom
                RefreshListView();
                SelectProgram(prog);
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to move program to bottom", ex);
                ShowNotification($"Failed to move program to bottom: {ex.Message}");
            }
        }

        private async Task SaveOrderAsync()
        {
            try
            {
                // Snapshot on the UI thread; the background save never touches the live list
                var snapshot = _model.Snapshot();
                await Task.Run(() => _registry.SaveStartupOrder(snapshot.EnabledInOrder().ToList()));
                ShowNotification("Order saved!");
                LoggingService.LogInfo("Order saved");
                SetDirty(false);
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
            if (_settings.GetSilenceNotifications()) return; // Check your setting
            notifyIcon.BalloonTipTitle = "Startup Controller";
            notifyIcon.BalloonTipText = text;
            notifyIcon.ShowBalloonTip(3000); // Show for 3 seconds
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_settings.GetStartToTray()) // Your setting
            {
                this.Hide();
                this.ShowInTaskbar = false;
            }
        }
    }
}
