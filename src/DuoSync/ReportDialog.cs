namespace DuoSync;

/// <summary>A status report as text (§6а): the other side's one to read, or one's own before sending it.</summary>
static class ReportDialog
{
    /// <summary>With <paramref name="send"/> there are two buttons and the answer says whether to send.</summary>
    public static bool Show(IWin32Window? owner, string title, string hint, string text, string? send = null)
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = true, ClientSize = new Size(760, 520), MinimumSize = new Size(480, 320),
            Font = new Font("Segoe UI", 10f),
        };
        var note = new Label { Text = hint, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(730, 0), Padding = new Padding(0, 0, 0, 6) };
        var body = new TextBox
        {
            Text = text.ReplaceLineEndings("\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = send == null ? "Закрыть" : "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(close);
        if (send != null)
        {
            var ok = new Button { Text = send, AutoSize = true, DialogResult = DialogResult.OK };
            buttons.Controls.Add(ok);
            form.AcceptButton = ok;
        }
        form.CancelButton = close;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(note);
        layout.Controls.Add(body);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.Shown += (_, _) => { body.SelectionLength = 0; close.Focus(); };
        return form.ShowDialog(owner) == DialogResult.OK;
    }
}
