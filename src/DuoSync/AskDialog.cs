namespace DuoSync;

/// <summary>Claude's question during a merge (§5.7): the question as Claude wrote it, and the person's answer.</summary>
static class AskDialog
{
    public static string? Ask(IWin32Window? owner, string question)
    {
        using var form = new Form
        {
            Text = "Claude спрашивает", FormBorderStyle = FormBorderStyle.Sizable, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(560, 300), MinimumSize = new Size(420, 260),
            Font = new Font("Segoe UI", 10f), ShowInTaskbar = true, TopMost = true,
        };
        var text = new TextBox
        {
            Text = question.ReplaceLineEndings("\r\n"), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = SystemColors.Control, TabStop = false,
        };
        var hint = new Label { Text = "Ответ (номер варианта или своими словами). Пока ответа нет, ничего не применяется.", AutoSize = true, ForeColor = Color.DimGray };
        var answer = new TextBox { Dock = DockStyle.Fill };
        var ok = new Button { Text = "Ответить", AutoSize = true, DialogResult = DialogResult.OK };
        var later = new Button { Text = "Потом", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { later, ok });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(text);
        layout.Controls.Add(hint);
        layout.Controls.Add(answer);
        layout.Controls.Add(buttons);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = later;
        form.Shown += (_, _) => answer.Focus();
        return form.ShowDialog(owner) == DialogResult.OK && answer.Text.Trim().Length > 0 ? answer.Text.Trim() : null;
    }
}
