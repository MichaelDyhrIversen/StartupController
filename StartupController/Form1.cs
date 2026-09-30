using System.Diagnostics;
using System.Net.WebSockets;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StartupController
{
    public partial class Form1 : Form, INotifier
    {
        public bool LaunchFromStartup = false;
        private readonly IUserSettings _settings;
        private readonly IStartupRegistry _registry;
        private readonly IProgramLauncher _launcher;
        private readonly StartupListModel _model = new StartupListModel();
        private readonly OrderSaveCoordinator _saver;

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
            _saver = new OrderSaveCoordinator(_model, _registry, this);

            InitializeComponent();
            btnEnable.Click += (s, e) => EnableSelectedProgram();
            btnDisable.Click += (s, e) => DisableSelectedProgram();
            btnLaunch.Click += (s, e) => LaunchSelectedProgram();
            btnMoveUp.Click += (s, e) => MoveSelectedProgram(-1);
            btnMoveTop.Click += (s, e) => MoveSelectedProgramToTop();
            btnMoveDown.Click += (s, e) => MoveSelectedProgram(1);
            btnMoveBottom.Click += (s, e) => MoveSelectedProgramToBottom();
            btnSaveOrder.Click += (s, e) => RunSaveAsync(manual: true);
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
                LoggingService.LogInfo($"AutoSave {(chkAutoSaveOnChange.Checked ? "on" : "off")}");
                // Switching AutoSave on saves pending changes right away
                if (chkAutoSaveOnChange.Checked && _model.IsDirty)
                    RunSaveAsync(manual: false);
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

            // Synchronous so e.Cancel is honoured and a save finishes before the process exits
            this.FormClosing += (s, e) => HandleFormClosing(e);

            // initial column sizing
            AdjustListViewColumns();
        }

        private void HandleFormClosing(FormClosingEventArgs e)
        {
            bool dirty = _model.IsDirty;
            if (ClosePolicy.Decide(e.CloseReason, dirty) == CloseDecision.CloseSilently)
            {
                if (dirty)
                {
                    // Windows shutdown/logoff: nothing new is written (D5)
                    LoggingService.LogWarning("Unsaved changes discarded at shutdown");
                    _saver.WaitForRunningSave();
                }
                else if (e.CloseReason != CloseReason.WindowsShutDown && _saver.HasPendingSaves)
                {
                    // An autosave hasn't finished: write the latest list before exiting
                    SaveBeforeClose(e);
                }
                else
                {
                    _saver.WaitForRunningSave();
                }
                return;
            }

            var res = MessageBox.Show("There are unsaved changes. Save before exiting?", "Unsaved Changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (res == DialogResult.Cancel)
            {
                LoggingService.LogInfo("Exit cancelled: unsaved changes");
                e.Cancel = true;
                return;
            }

            if (res == DialogResult.Yes)
            {
                SaveBeforeClose(e);
                return;
            }

            LoggingService.LogInfo("Unsaved changes discarded on exit");
            // Known, accepted race: an autosave queued just before "No" (possible only if the list was dirty from a
            // failed autosave) may still write after this point. It writes a snapshot the user already made.
            _saver.WaitForRunningSave();
        }

        // Synchronous save while closing; a failure keeps the window open
        private void SaveBeforeClose(FormClosingEventArgs e)
        {
            if (_saver.SaveNow(out var error)) return;

            e.Cancel = true;
            UpdateSaveButton();
            MessageBox.Show($"Failed to save order: {error}", "Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // Called after every list change (dirty = true)
        private void SetDirty(bool dirty)
        {
            if (dirty && _settings.GetAutoSaveOnChange())
            {
                // The list stays clean unless the save fails (OrderSaveCoordinator marks it dirty then)
                RunSaveAsync(manual: false);
                return;
            }

            if (dirty)
                _model.MarkDirty();
            else
                _model.MarkClean();
            UpdateSaveButton();
        }

        // Visual cue on the save button while there are unsaved changes (also after a failed autosave)
        private void UpdateSaveButton()
        {
            if (IsDisposed || Disposing) return; // a save can finish after the form has closed
            btnSaveOrder.Enabled = true; // always enabled so the user can save if they want
            btnSaveOrder.BackColor = _model.IsDirty ? System.Drawing.Color.LightSalmon : btnDisable.BackColor;
        }

        // Fire-and-forget entry point for UI events (Save button, autosave); failures are handled here, not dropped.
        // A manual save notifies "Order saved!"; failures are logged and notified by the coordinator.
        private async void RunSaveAsync(bool manual)
        {
            try
            {
                if (manual)
                    LoggingService.LogInfo("Save order requested");
                await _saver.SaveAsync(manual);
                UpdateSaveButton();
            }
            catch (Exception ex)
            {
                _model.MarkDirty();
                LoggingService.LogError(manual ? "Failed to save order" : "Autosave failed", ex);
                try
                {
                    UpdateSaveButton();
                }
                catch (Exception uiEx)
                {
                    LoggingService.LogError("Failed to update the save button", uiEx);
                }
            }
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
            StatusText(prog),
            prog.Path,
            prog.Description
        });
                item.Tag = prog;
                listViewStartup.Items.Add(item);
            }

            AdjustListViewColumns();
        }

        // Status column. Changed (D7): stored as enabled, but the Run data changed, so it isn't launched.
        internal static string StatusText(StartupProgram prog)
        {
            if (prog.Changed) return "Changed – re-enable to launch";
            return prog.Enabled ? "Enabled" : "Disabled";
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

        // Enable/Disable change only the app's own launch list (in memory until saved), never StartupApproved
        private void ToggleSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                if (!_model.Toggle(prog)) return;
                LoggingService.LogInfo($"{(prog.Enabled ? "Enabled" : "Disabled")} in launch list: {prog.Name}");
                RefreshListView();
                SetDirty(true);
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Failed to toggle program", ex);
                ShowNotification($"Failed to toggle program: {ex.Message}");
            }
        }

        private void EnableSelectedProgram()
        {
            if (SelectedProgram() is not StartupProgram prog) return;
            try
            {
                if (!_model.Enable(prog)) return;
                LoggingService.LogInfo($"Enabled in launch list: {prog.Name}");
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
                if (!_model.Disable(prog)) return;
                LoggingService.LogInfo($"Disabled in launch list: {prog.Name}");
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

        private void ShowHelp()
        {
            MessageBox.Show("This application allows you to manage disabled startup programs. Select a program to enable, disable, launch, or reorder it. Use Save Order to persist your preferred startup sequence.");
        }
        public void ShowStartupNotification(string programName, int current, int total)
        {
            ShowNotification($"Starting {programName} ({current} of {total})");
        }

        void INotifier.Notify(string message) => ShowNotification(message);

        private void ShowNotification(string text)
        {
            if (IsDisposed || Disposing) return; // e.g. a save notification arriving after the form closed
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
