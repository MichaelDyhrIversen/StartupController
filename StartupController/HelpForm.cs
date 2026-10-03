namespace StartupController
{
    // The Help window: section list (with search) on the left, the selected section on the right. Modeless and
    // unowned (Form1 keeps one instance), so it also opens from the tray while the main window is hidden.
    // Content is plain text: the RichTextBox never gets RTF and doesn't detect URLs.
    internal sealed class HelpForm : Form
    {
        private readonly IReadOnlyList<HelpSection> _sections;
        private readonly List<int> _shown = new List<int>(); // section index per list row
        private readonly TextBox _search;
        private readonly ListBox _list;
        private readonly RichTextBox _content;
        private readonly SplitContainer _split;
        private readonly Font _bodyFont;
        private readonly Font _titleFont;
        private int _current = -1; // section shown on the right
        private bool _updatingList;

        internal HelpForm(IReadOnlyList<HelpSection> sections)
        {
            _sections = sections;
            _bodyFont = new Font(SystemFonts.MessageBoxFont ?? Control.DefaultFont, FontStyle.Regular);
            _titleFont = new Font(_bodyFont.FontFamily, _bodyFont.SizeInPoints * 1.3f, FontStyle.Bold, GraphicsUnit.Point);

            SuspendLayout();
            ChildWindow.Setup(this, "StartupController Help", new Size(760, 520), new Size(520, 360));

            _search = new TextBox
            {
                Dock = DockStyle.Top,
                PlaceholderText = "Search help",
                TabIndex = 0,
                AccessibleName = "Search help"
            };
            _search.TextChanged += (s, e) => ApplySearch();

            _list = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                TabIndex = 1,
                AccessibleName = "Help sections"
            };
            _list.SelectedIndexChanged += (s, e) => ShowSelected();

            var gap = new Panel { Dock = DockStyle.Top, Height = 6 };

            _content = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                DetectUrls = false,
                BorderStyle = BorderStyle.None,
                BackColor = SystemColors.Window,
                ForeColor = SystemColors.WindowText,
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                TabIndex = 0,
                AccessibleName = "Help text"
            };

            var contentPanel = new Panel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(10, 8, 6, 6),
                BackColor = SystemColors.Window
            };
            contentPanel.Controls.Add(_content);

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                TabIndex = 0
            };
            _split.Panel1.Padding = new Padding(8, 8, 0, 8);
            _split.Panel1.Controls.Add(_list);
            _split.Panel1.Controls.Add(gap);
            _split.Panel1.Controls.Add(_search);
            _split.Panel2.Padding = new Padding(0, 8, 8, 8);
            _split.Panel2.Controls.Add(contentPanel);

            var buttons = ChildWindow.CreateButtonRow(this);
            buttons.TabIndex = 1;

            Controls.Add(_split);
            Controls.Add(buttons);

            ResumeLayout(performLayout: true);

            FillList(Enumerable.Range(0, _sections.Count));
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Set after scaling, from the font height, so the list keeps its share at any DPI
            try
            {
                _split.Panel1MinSize = Font.Height * 10;
                _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Font.Height * 13);
            }
            catch (InvalidOperationException)
            {
                // window too small for the preferred width: keep the default split
            }
            _list.Focus();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _titleFont.Dispose();
                _bodyFont.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ApplySearch()
        {
            FillList(HelpContent.Search(_sections, _search.Text));
        }

        // Rebuilds the list; the shown section stays if it still matches, otherwise the first match is shown
        private void FillList(IEnumerable<int> indices)
        {
            _updatingList = true;
            try
            {
                _shown.Clear();
                _shown.AddRange(indices);
                _list.BeginUpdate();
                _list.Items.Clear();
                foreach (var i in _shown)
                    _list.Items.Add(_sections[i].Title);
                _list.EndUpdate();

                int row = _shown.IndexOf(_current);
                if (row < 0 && _shown.Count > 0) row = 0;
                _list.SelectedIndex = row;
            }
            finally
            {
                _updatingList = false;
            }
            ShowSelected();
        }

        private void ShowSelected()
        {
            if (_updatingList) return;
            int row = _list.SelectedIndex;
            if (row < 0 || row >= _shown.Count)
            {
                _current = -1;
                ShowText("No matches", "No help section contains \"" + _search.Text.Trim() + "\".");
                return;
            }
            if (_shown[row] == _current) return;
            _current = _shown[row];
            ShowText(_sections[_current].Title, _sections[_current].Body);
        }

        // Plain text only: title in bold, body regular. Never assigns Rtf.
        private void ShowText(string title, string body)
        {
            _content.Clear();
            _content.SelectionFont = _titleFont;
            _content.AppendText(title + "\n\n");
            _content.SelectionFont = _bodyFont;
            _content.AppendText(body);
            _content.Select(0, 0);
            _content.ScrollToCaret();
        }
    }
}
