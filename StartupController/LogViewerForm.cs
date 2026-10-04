namespace StartupController
{
    // Read-only log viewer. Modeless and unowned (Form1 keeps one instance). The log is read off the UI thread with
    // LogFileReader (shared open, closed right away, last 4 MB only), parsed into LogLine and shown as plain text in
    // a virtual ListView. Nothing in the log is ever opened, run or unescaped. Refreshing writes nothing to the log,
    // so auto-refresh can't trigger itself.
    internal sealed class LogViewerForm : Form
    {
        private const string Caption = "StartupController Log";
        private const int ToolTipMaxLength = 1000;
        private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(2);

        // Level presets of the "Show" box
        private static readonly (string Text, LogLevelFilter Levels)[] LevelPresets =
        {
            ("All levels", LogLevelFilter.All),
            ("Errors and warnings", LogLevelFilter.Problems),
            ("Launches", LogLevelFilter.Launch),
        };

        private readonly IProcessStarter _starter;
        private readonly string _logFilePath;

        private readonly ComboBox _fileBox;
        private readonly ComboBox _levelBox;
        private readonly TextBox _search;
        private readonly CheckBox _sessionOnly;
        private readonly CheckBox _autoRefresh;
        private readonly Button _refresh;
        private readonly ListView _list;
        private readonly Label _emptyLabel;
        private readonly Button _copy;
        private readonly Button _copyAll;
        private readonly Button _openInEditor;
        private readonly ToolStripStatusLabel _countLabel;
        private readonly ToolStripStatusLabel _truncatedLabel;
        private readonly ToolStripStatusLabel _fileLabel;
        private readonly System.Windows.Forms.Timer _searchTimer;
        private readonly System.Windows.Forms.Timer _autoRefreshTimer;
        private readonly HashSet<string> _loggedErrorKinds = new HashSet<string>(StringComparer.Ordinal);

        private IReadOnlyList<LogLine> _all = Array.Empty<LogLine>();
        private IReadOnlyList<LogLine> _shown = Array.Empty<LogLine>();
        private LogReadResult? _lastRead;
        private string? _lastReadPath;
        private int _refreshId;          // a newer refresh makes older results stale
        private bool _refreshInFlight;   // auto-refresh skips a tick while a read runs
        private bool _updatingFileBox;

        internal LogViewerForm(IProcessStarter starter, string logFilePath)
        {
            ArgumentNullException.ThrowIfNull(starter);
            ArgumentException.ThrowIfNullOrEmpty(logFilePath);
            _starter = starter;
            _logFilePath = logFilePath;

            SuspendLayout();
            ChildWindow.Setup(this, Caption, new Size(900, 560), new Size(640, 360));

            // ---- top bar ----
            _fileBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170, AccessibleName = "Log file" };
            _fileBox.SelectedIndexChanged += (s, e) =>
            {
                if (_updatingFileBox) return;
                // Which file only, not its path: the rotated files sit next to the current log
                LoggingService.LogInfo("Log viewer switched to: " + _fileBox.Text);
                UpdateEditorButton();
                RunRefresh(scrollToEnd: true);
            };

            _levelBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, AccessibleName = "Levels" };
            foreach (var preset in LevelPresets)
                _levelBox.Items.Add(preset.Text);
            _levelBox.SelectedIndex = 0;
            _levelBox.SelectedIndexChanged += (s, e) => ApplyFilter(resetSelection: true);

            _search = new TextBox { Width = 180, PlaceholderText = "Search", AccessibleName = "Search the log" };
            _search.TextChanged += (s, e) =>
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            };

            _sessionOnly = new CheckBox { Text = "Current session only", AutoSize = true };
            _sessionOnly.CheckedChanged += (s, e) => ApplyFilter(resetSelection: true);

            _autoRefresh = new CheckBox { Text = "Auto-refresh", AutoSize = true, Checked = false };
            _autoRefresh.CheckedChanged += (s, e) => _autoRefreshTimer.Enabled = _autoRefresh.Checked;

            _refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(80, 27) };
            _refresh.Click += (s, e) => RunRefresh(scrollToEnd: false);

            var top = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(6, 6, 6, 2)
            };
            top.Controls.Add(Labelled("File:", _fileBox));
            top.Controls.Add(Labelled("Show:", _levelBox));
            top.Controls.Add(_search);
            top.Controls.Add(_sessionOnly);
            top.Controls.Add(_autoRefresh);
            top.Controls.Add(_refresh);
            foreach (Control c in top.Controls)
                c.Margin = new Padding(3, 4, 9, 3);

            // ---- body ----
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                VirtualMode = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                ShowItemToolTips = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                AccessibleName = "Log lines"
            };
            _list.Columns.Add("Time");
            _list.Columns.Add("Level");
            _list.Columns.Add("Category");
            _list.Columns.Add("Message");
            _list.RetrieveVirtualItem += (s, e) => e.Item = CreateItem(e.ItemIndex);
            _list.KeyDown += OnListKeyDown;
            _list.SelectedIndexChanged += (s, e) => UpdateCopyButton();
            _list.VirtualItemsSelectionRangeChanged += (s, e) => UpdateCopyButton();
            _list.ClientSizeChanged += (s, e) => AdjustColumns();

            _emptyLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "No log has been written yet.",
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.GrayText,
                Visible = false
            };

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
            body.Controls.Add(_list);
            body.Controls.Add(_emptyLabel);

            // ---- buttons ----
            var openFolder = ChildWindow.CreateButton("Open log folder");
            openFolder.Click += (s, e) => OpenLogFolder();
            _openInEditor = ChildWindow.CreateButton("Open in editor");
            _openInEditor.Click += (s, e) => OpenInEditor();
            _copyAll = ChildWindow.CreateButton("Copy all shown");
            _copyAll.Click += (s, e) => CopyLines(_shown, "Copy all shown");
            _copy = ChildWindow.CreateButton("Copy");
            _copy.Enabled = false;
            _copy.Click += (s, e) => CopySelected();

            var buttons = ChildWindow.CreateButtonRow(this, openFolder, _openInEditor, _copyAll, _copy);

            // ---- status bar ----
            _countLabel = new ToolStripStatusLabel();
            _truncatedLabel = new ToolStripStatusLabel { Visible = false };
            _fileLabel = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            var status = new StatusStrip { SizingGrip = true };
            status.Items.Add(_countLabel);
            status.Items.Add(_truncatedLabel);
            status.Items.Add(_fileLabel);

            // Fill first, then the edges (docking runs in reverse order of adding)
            Controls.Add(body);
            Controls.Add(top);
            Controls.Add(buttons);
            Controls.Add(status);

            _searchTimer = new System.Windows.Forms.Timer { Interval = (int)SearchDelay.TotalMilliseconds };
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                ApplyFilter(resetSelection: true);
            };
            _autoRefreshTimer = new System.Windows.Forms.Timer { Interval = (int)AutoRefreshInterval.TotalMilliseconds };
            _autoRefreshTimer.Tick += (s, e) => AutoRefreshTick();

            ResumeLayout(performLayout: true);

            FillFileBox();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            AdjustColumns();
            RunRefresh(scrollToEnd: true);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _list.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _autoRefreshTimer.Stop();
            _searchTimer.Stop();
            _refreshId++; // a read still running is dropped
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _autoRefreshTimer.Dispose();
                _searchTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        // F5 refreshes, whatever control has focus
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5)
            {
                RunRefresh(scrollToEnd: false);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static Control Labelled(string text, Control control)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
            var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 3, 0) };
            control.Margin = Padding.Empty;
            panel.Controls.Add(label);
            panel.Controls.Add(control);
            return panel;
        }

        // ---------- files ----------

        private string SelectedPath => (_fileBox.SelectedItem as LogFileChoice)?.Path ?? _logFilePath;

        private bool CurrentLogSelected => string.Equals(SelectedPath, _logFilePath, StringComparison.OrdinalIgnoreCase);

        // The current log is always offered (it may not exist yet); rotated logs only when they exist
        private void FillFileBox()
        {
            var selected = SelectedPath;
            var choices = new List<LogFileChoice> { new LogFileChoice("Current log", _logFilePath) };
            for (int i = 1; i < LoggingService.KeptLogFiles; i++)
            {
                var path = LoggingService.RotatedPath(_logFilePath, i);
                if (File.Exists(path))
                    choices.Add(new LogFileChoice(i == 1 ? $"Previous log (.{i})" : $"Older log (.{i})", path));
            }
            // Unchanged: leave the box alone (rebuilding it would close an open drop-down on every auto-refresh)
            if (_fileBox.Items.Cast<LogFileChoice>().SequenceEqual(choices)) return;

            _updatingFileBox = true;
            try
            {
                _fileBox.BeginUpdate();
                _fileBox.Items.Clear();
                foreach (var choice in choices)
                    _fileBox.Items.Add(choice);
                _fileBox.EndUpdate();
                int index = choices.FindIndex(c => string.Equals(c.Path, selected, StringComparison.OrdinalIgnoreCase));
                _fileBox.SelectedIndex = index < 0 ? 0 : index;
            }
            finally
            {
                _updatingFileBox = false;
            }
            UpdateEditorButton();
        }

        private void UpdateEditorButton() => _openInEditor.Enabled = CurrentLogSelected;

        private sealed record LogFileChoice(string Text, string Path)
        {
            public override string ToString() => Text;
        }

        // ---------- reading ----------

        // Fire-and-forget entry point for UI events; failures are handled here, not dropped
        private async void RunRefresh(bool scrollToEnd)
        {
            try
            {
                await RefreshAsync(scrollToEnd);
            }
            catch (Exception ex)
            {
                OnRefreshFailed(ex);
            }
        }

        // A failed refresh writes to the log, which changes the file, which would trigger the next auto-refresh and
        // so on. So: logged once per kind of exception, and auto-refresh is turned off (the user can turn it back on).
        private void OnRefreshFailed(Exception ex)
        {
            if (_loggedErrorKinds.Add("refresh:" + ex.GetType().FullName))
                LoggingService.LogError("Log viewer refresh failed", ex);
            if (IsDisposed || Disposing) return;
            _autoRefresh.Checked = false; // stops the timer (CheckedChanged)
            _fileLabel.Text = "Refresh failed: " + LogEscape.Escape(ex.Message) + ". Auto-refresh is off.";
        }

        // Reads and parses off the UI thread; the result is applied only if no newer refresh started meanwhile
        private async Task RefreshAsync(bool scrollToEnd)
        {
            if (IsDisposed) return;
            FillFileBox();
            int id = ++_refreshId;
            var path = SelectedPath;
            _refreshInFlight = true;
            try
            {
                var (result, lines) = await Task.Run(() =>
                {
                    var read = LogFileReader.ReadTail(path, LogFileReader.MaxViewBytes);
                    IReadOnlyList<LogLine> parsed = read.Lines.Select(LogLine.Parse).ToList();
                    return (read, parsed);
                });
                if (IsDisposed || Disposing || id != _refreshId) return;

                bool otherFile = !string.Equals(path, _lastReadPath, StringComparison.OrdinalIgnoreCase);
                // The selection survives only a pure append (same file, not shorter, same lines plus new ones), so
                // Copy never copies lines the user didn't pick. ApplyFilter checks the shown rows the same way.
                bool appendOnly = !otherFile && _lastRead != null && result.Exists && result.Error == null
                    && result.FileLength >= _lastRead.FileLength && LogFilter.IsAppendOf(_all, lines);
                // Follow the end unless the user scrolled up in the same file
                bool keepPosition = !scrollToEnd && !otherFile && !IsLastRowVisible();
                _lastRead = result;
                _lastReadPath = path;
                _all = lines;
                ShowReadResult(result, path);
                ApplyFilter(resetSelection: !appendOnly, scrollToEnd: !keepPosition);
            }
            finally
            {
                if (id == _refreshId) _refreshInFlight = false;
            }
        }

        private void ShowReadResult(LogReadResult result, string path)
        {
            _truncatedLabel.Visible = result.Truncated;
            _truncatedLabel.Text = LogFileReader.TruncationText(result);

            if (result.Error != null)
            {
                _fileLabel.Text = "Could not read the log: " + result.Error;
                // Logged once per kind of error, so auto-refresh doesn't fill the log with the same failure
                if (_loggedErrorKinds.Add(result.ErrorKind ?? ""))
                    LoggingService.LogWarning("Log viewer could not read the log: " + result.Error);
            }
            else if (!result.Exists)
            {
                _fileLabel.Text = path;
            }
            else
            {
                _fileLabel.Text = $"{path}  (last written {result.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})";
            }

            bool missing = !result.Exists;
            _emptyLabel.Visible = missing;
            _list.Visible = !missing;
        }

        // Cheap check every 2 s: re-read only when the file's length, write time or existence changed
        private void AutoRefreshTick()
        {
            if (_refreshInFlight || IsDisposed) return;
            try
            {
                var info = new FileInfo(SelectedPath);
                var last = _lastRead;
                bool changed = last == null
                    || info.Exists != last.Exists
                    || (info.Exists && (info.Length != last.FileLength || info.LastWriteTimeUtc != last.LastWriteTimeUtc));
                if (changed)
                    RunRefresh(scrollToEnd: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                RunRefresh(scrollToEnd: false); // let the reader report it in the status bar
            }
        }

        // ---------- list ----------

        private LogLevelFilter SelectedLevels =>
            _levelBox.SelectedIndex >= 0 && _levelBox.SelectedIndex < LevelPresets.Length ? LevelPresets[_levelBox.SelectedIndex].Levels : LogLevelFilter.All;

        // Filtering is in memory on the UI thread (at most LogFileReader.MaxViewLines lines)
        private void ApplyFilter(bool resetSelection, bool scrollToEnd = true)
        {
            if (IsDisposed) return;
            var filtered = LogFilter.Apply(_all, SelectedLevels, _search.Text, _sessionOnly.Checked);

            _list.BeginUpdate();
            try
            {
                // Selected row numbers stay valid only if the old rows are still the first rows, in order
                if (resetSelection || !LogFilter.IsAppendOf(_shown, filtered))
                    _list.SelectedIndices.Clear();
                _shown = filtered;
                _list.VirtualListSize = filtered.Count;
                _list.Invalidate();
            }
            finally
            {
                _list.EndUpdate();
            }

            // Otherwise the list keeps its scroll position (new lines are added below)
            if (scrollToEnd && filtered.Count > 0)
                _list.EnsureVisible(filtered.Count - 1);

            _countLabel.Text = $"{filtered.Count} of {_all.Count} lines";
            _copyAll.Enabled = filtered.Count > 0;
            UpdateCopyButton();
        }

        private bool IsLastRowVisible()
        {
            int count = _list.VirtualListSize;
            if (count == 0 || !_list.IsHandleCreated) return true;
            try
            {
                var rect = _list.GetItemRect(count - 1);
                return rect.Top < _list.ClientSize.Height && rect.Bottom > 0;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private ListViewItem CreateItem(int index)
        {
            if (index < 0 || index >= _shown.Count)
                return new ListViewItem(new[] { "", "", "", "" });

            var line = _shown[index];
            var item = new ListViewItem(new[] { line.Time, line.Level, line.Category, line.Message })
            {
                ToolTipText = LogLine.Shorten(line.Message, ToolTipMaxLength)
            };
            var color = LevelColor(line);
            if (color is Color c)
                item.ForeColor = c;
            return item;
        }

        // ERROR and failed launches red, WARN dark orange. High contrast: no custom colours.
        private static Color? LevelColor(LogLine line)
        {
            if (SystemInformation.HighContrast) return null;
            if (line.LaunchFailed || line.Level == "ERROR") return Color.FromArgb(192, 0, 0);
            if (line.Level == "WARN") return Color.FromArgb(168, 80, 0);
            return null;
        }

        private void AdjustColumns()
        {
            if (_list.Columns.Count < 4) return;
            int pad = TextRenderer.MeasureText("MM", _list.Font).Width;
            int time = TextRenderer.MeasureText("0000-00-00 00:00:00.000", _list.Font).Width + pad;
            int level = TextRenderer.MeasureText("SESSION", _list.Font).Width + pad;
            int category = TextRenderer.MeasureText("Program", _list.Font).Width + pad;
            int message = Math.Max(pad * 10, _list.ClientSize.Width - time - level - category - 4);

            _list.BeginUpdate();
            try
            {
                _list.Columns[0].Width = time;
                _list.Columns[1].Width = level;
                _list.Columns[2].Width = category;
                _list.Columns[3].Width = message;
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        // ---------- copy ----------

        private void OnListKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelected();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
            else if (e.Control && e.KeyCode == Keys.A)
            {
                SelectAll();
                e.SuppressKeyPress = true;
                e.Handled = true;
            }
        }

        // One LVM_SETITEMSTATE for all rows (item -1) instead of one call per row, so Ctrl+A stays instant at 50k rows
        private void SelectAll()
        {
            if (_list.VirtualListSize == 0 || !_list.IsHandleCreated) return;
            var item = new NativeMethods.LVITEMW
            {
                mask = NativeMethods.LVIF_STATE,
                state = NativeMethods.LVIS_SELECTED,
                stateMask = NativeMethods.LVIS_SELECTED
            };
            NativeMethods.SendMessage(_list.Handle, NativeMethods.LVM_SETITEMSTATE, new IntPtr(-1), ref item);
            UpdateCopyButton();
        }

        private void UpdateCopyButton() => _copy.Enabled = _list.SelectedIndices.Count > 0;

        private void CopySelected()
        {
            int selected = _list.SelectedIndices.Count;
            if (selected == 0) return;
            if (selected >= _shown.Count)
            {
                CopyLines(_shown, "Copy"); // everything is selected (Ctrl+A): no need to walk the selection
                return;
            }
            var lines = new List<LogLine>(selected);
            foreach (int index in _list.SelectedIndices)
            {
                if (index >= 0 && index < _shown.Count)
                    lines.Add(_shown[index]);
            }
            CopyLines(lines, "Copy");
        }

        // The lines as in the file (tab-separated), one per line, with every field escaped again (LogLine.CopyText).
        // Only the number of lines is logged, never their content.
        private void CopyLines(IReadOnlyList<LogLine> lines, string action)
        {
            if (lines.Count == 0) return;
            var text = string.Join("\r\n", lines.Select(l => l.CopyText));
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                _countLabel.Text = lines.Count == 1 ? "1 line copied" : $"{lines.Count} lines copied";
                LoggingService.LogInfo($"Log viewer: {action}, {lines.Count} line(s) copied");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or ThreadStateException)
            {
                LoggingService.LogError("Could not copy log lines to the clipboard", ex);
                MessageBox.Show(this, "Could not copy to the clipboard: " + ex.Message, Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ---------- open ----------

        // The log this viewer was given, in the external editor (created if it is missing). A failure is logged by LoggingService.
        private void OpenInEditor()
        {
            if (LoggingService.OpenLogFile(_starter, _logFilePath, out var error)) return;
            ShowOpenFailure("the log file", error, "The log is at:", _logFilePath);
        }

        // The folder of the log this viewer was given. A failure is logged by LoggingService.
        private void OpenLogFolder()
        {
            var folder = Path.GetDirectoryName(_logFilePath);
            if (string.IsNullOrEmpty(folder))
            {
                LoggingService.LogWarning("Could not open the log folder: the log path has no folder");
                ShowOpenFailure("the log folder", "The log path has no folder.", "The log is at:", _logFilePath);
                return;
            }
            if (LoggingService.OpenLogFolder(_starter, folder, out var error)) return;
            ShowOpenFailure("the log folder", error, "The log folder is:", folder);
        }

        private void ShowOpenFailure(string what, string? error, string whereLabel, string where)
        {
            MessageBox.Show(this, $"Could not open {what}: {error}\n\n{whereLabel}\n{where}", Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
