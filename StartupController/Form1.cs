using System.Diagnostics;
using System.Net.WebSockets;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StartupController
{
    public partial class Form1 : Form, INotifier, IMessageDialog
    {
        public bool LaunchFromStartup = false;
        private readonly IUserSettings _settings;
        private readonly IStartupRegistry _registry;
        private readonly IProgramLauncher _launcher;
        private readonly StartupListModel _model = new StartupListModel();
        private readonly OrderSaveCoordinator _saver;
        private readonly LaunchRunner _runner;
        private readonly SettingsController _settingsController;

        private bool _suppressSettingEvents; // set while a failed setting change reverts its checkbox
        private bool _firstShowHandled;       // SetVisibleCore has seen the first show request
        private bool _startupWorkStarted;     // RunStartupAsync runs once, whether the window is shown or not
        private bool _manualLaunchInFlight;   // the Launch button ignores clicks until the current launch returns
        private int _restoreRequested;        // 1 = a second instance asked for the window (set from another thread)

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
            _runner = new LaunchRunner(_launcher, this, this);
            _settingsController = new SettingsController(_settings, _registry, this, Application.ExecutablePath);

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

            notifyIcon.DoubleClick += (s, e) => RestoreFromTray();
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
            // Settings checkboxes: a failed write is logged and notified, and the checkbox reverts
            chkLaunchToTray.CheckedChanged += (s, e) => ApplySetting(chkLaunchToTray, _settingsController.ApplyStartToTray);
            chkSilenceNotifications.Checked = _settings.GetSilenceNotifications();
            chkSilenceNotifications.CheckedChanged += (s, e) => ApplySetting(chkSilenceNotifications, _settingsController.ApplySilenceNotifications);
            chkLaunchProgramsOnStartup.Checked = _settings.GetLaunchProgramsOnStartup();
            chkLaunchProgramsOnStartup.CheckedChanged += (s, e) => ApplySetting(chkLaunchProgramsOnStartup, _settingsController.ApplyLaunchProgramsOnStartup);

            // autosave checkbox
            chkAutoSaveOnChange.Checked = _settings.GetAutoSaveOnChange();
            chkAutoSaveOnChange.CheckedChanged += (s, e) =>
            {
                // Switching AutoSave on saves pending changes right away
                if (ApplySetting(chkAutoSaveOnChange, _settingsController.ApplyAutoSaveOnChange) && chkAutoSaveOnChange.Checked && _model.IsDirty)
                    RunSaveAsync(manual: false);
            };

            // Also started from SetVisibleCore when the window starts hidden (Load then waits for the first real show)
            this.Load += (s, e) => BeginStartupWork();

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

        // Applies a settings checkbox change; on failure reverts the checkbox without re-running the handler.
        // Returns true when the change was saved.
        private bool ApplySetting(CheckBox box, Func<bool, bool> apply)
        {
            if (_suppressSettingEvents) return false;

            bool requested = box.Checked;
            bool actual = apply(requested);
            if (actual == requested) return true;

            _suppressSettingEvents = true;
            try
            {
                box.Checked = actual;
            }
            finally
            {
                _suppressSettingEvents = false;
            }
            return false;
        }

        // Read once, at the first show or the start of the startup work (whichever comes first), so visibility and
        // launch-and-exit are decided from the same settings even if a checkbox changes later.
        // LaunchMode: --launch with "Launch programs on startup" on (launch the list, then exit).
        // StartHidden: Start to tray, or launch mode: the window is not shown at startup.
        private (bool LaunchMode, bool StartHidden)? _startupMode;

        private (bool LaunchMode, bool StartHidden) StartupMode
        {
            get
            {
                if (_startupMode == null)
                {
                    bool launchMode = LaunchFromStartup && _settings.GetLaunchProgramsOnStartup();
                    _startupMode = (launchMode, launchMode || _settings.GetStartToTray());
                }
                return _startupMode.Value;
            }
        }

        private bool IsLaunchMode => StartupMode.LaunchMode;

        private bool StartHidden => StartupMode.StartHidden;

        // A second instance asked for the window (called on the activation thread). Before the handle exists the
        // request is remembered and honoured in OnHandleCreated instead of being dropped.
        internal void RequestRestore()
        {
            Interlocked.Exchange(ref _restoreRequested, 1);
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(ConsumeRestoreRequest));
            }
            catch (InvalidOperationException)
            {
                // Handle destroyed meanwhile (closing); nothing to show
            }
        }

        private void ConsumeRestoreRequest()
        {
            if (Interlocked.Exchange(ref _restoreRequested, 0) == 1)
                RestoreFromTray();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Volatile.Read(ref _restoreRequested) == 1)
                BeginInvoke(new Action(ConsumeRestoreRequest));
        }

        // The only place that decides startup visibility. Suppressing the first show (instead of hiding the
        // window after it appeared) avoids a flash. Load is raised only by a real show, so for a hidden start the
        // startup work is posted from here; Load runs later, when the user restores the window.
        protected override void SetVisibleCore(bool value)
        {
            if (value && !_firstShowHandled)
            {
                _firstShowHandled = true;
                if (StartHidden)
                {
                    if (!IsHandleCreated) CreateHandle();
                    base.SetVisibleCore(false);
                    BeginInvoke(new Action(BeginStartupWork));
                    return;
                }
            }
            base.SetVisibleCore(value);
        }

        // Shows the window from the tray (tray double-click, or a second instance being started)
        internal void RestoreFromTray()
        {
            if (IsDisposed || Disposing) return;
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.Activate();
            LoggingService.LogInfo("Window restored");
        }

        private async void BeginStartupWork()
        {
            if (_startupWorkStarted) return;
            _startupWorkStarted = true;
            try
            {
                await RunStartupAsync();
            }
            catch (Exception ex)
            {
                LoggingService.LogError("Startup failed", ex);
                if (IsLaunchMode) Application.Exit();
            }
        }

        private async Task RunStartupAsync()
        {
            LoggingService.StartSession(string.Join(' ', Environment.GetCommandLineArgs()));
            LoggingService.LogInfo("Loading startup programs");
            bool loaded = await StartupSession.LoadProgramsAsync(_registry, _model, this);
            if (loaded)
                RefreshListView();

            if (!IsLaunchMode) return;

            if (loaded)
                await _runner.LaunchSequenceAsync(_model.EnabledPrograms());
            else
                LoggingService.LogError("--launch: nothing launched because the startup list could not be loaded");

            // Give the last balloon time to show: Application.Exit disposes the tray icon
            var delay = StartupSession.ExitDelay(_settings.GetSilenceNotifications(), loadFailed: !loaded);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
            LoggingService.LogInfo("--launch finished, exiting");
            Application.Exit();
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

        // Logging, notifications and the blocked-entry dialog are handled by LaunchRunner
        // Clicks while a launch is running are ignored (the button is disabled meanwhile)
        private async void LaunchSelectedProgram()
        {
            if (_manualLaunchInFlight) return;
            if (SelectedProgram() is not StartupProgram prog) return;
            _manualLaunchInFlight = true;
            btnLaunch.Enabled = false;
            try
            {
                await _runner.LaunchManualAsync(prog);
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"Failed to launch {prog.Name}", ex);
            }
            finally
            {
                _manualLaunchInFlight = false;
                if (!IsDisposed) btnLaunch.Enabled = true;
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
        void INotifier.Notify(string message) => ShowNotification(message);

        void IMessageDialog.ShowWarning(string text, string caption)
        {
            if (IsDisposed || Disposing) return;
            MessageBox.Show(this, text, caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private void ShowNotification(string text)
        {
            if (IsDisposed || Disposing) return; // e.g. a save notification arriving after the form closed
            if (_settings.GetSilenceNotifications()) return; // Check your setting
            notifyIcon.BalloonTipTitle = "Startup Controller";
            notifyIcon.BalloonTipText = text;
            notifyIcon.ShowBalloonTip(3000); // Show for 3 seconds
        }
    }
}
