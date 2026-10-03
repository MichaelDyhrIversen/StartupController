namespace StartupController
{
    // Shared setup for the modeless child windows (Help, log viewer): same scaling as Form1 and a bottom-right
    // button row with Close, which Esc also triggers.
    internal static class ChildWindow
    {
        // Call between SuspendLayout and the first control. AutoScaleDimensions matches Form1, so all windows scale alike.
        internal static void Setup(Form form, string title, Size clientSize, Size minimumSize)
        {
            ArgumentNullException.ThrowIfNull(form);
            form.AutoScaleDimensions = new SizeF(7F, 15F);
            form.AutoScaleMode = AutoScaleMode.Font;
            form.Text = title;
            form.ClientSize = clientSize;
            form.MinimumSize = minimumSize;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowInTaskbar = true;
        }

        internal static Button CreateButton(string text) =>
            new Button { Text = text, AutoSize = true, MinimumSize = new Size(90, 30) };

        // The button row, docked at the bottom: Close on the right, then the given buttons from right to left.
        // Close closes the form and is its CancelButton. The caller adds the row to the form (docking order matters).
        internal static FlowLayoutPanel CreateButtonRow(Form form, params Button[] buttons)
        {
            ArgumentNullException.ThrowIfNull(form);
            var close = CreateButton("Close");
            close.TabIndex = 0;
            close.Click += (s, e) => form.Close();

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(8, 2, 8, 6)
            };
            row.Controls.Add(close);
            foreach (var button in buttons)
                row.Controls.Add(button);

            form.CancelButton = close; // Esc closes
            return row;
        }
    }
}
