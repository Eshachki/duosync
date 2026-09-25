namespace DuoSync;

/// <summary>Small modal text prompt (WinForms has no InputBox).</summary>
static class Prompt
{
    public static string? Ask(IWin32Window? owner, string title, string label, string initial = "")
    {
        using var form = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ClientSize = new Size(420, 120), Font = new Font("Segoe UI", 10f),
        };
        var text = new Label { Text = label, Left = 12, Top = 12, Width = 396, Height = 22 };
        var box = new TextBox { Left = 12, Top = 38, Width = 396, Text = initial };
        var ok = new Button { Text = "OK", Left = 232, Top = 76, Width = 84, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Отмена", Left = 324, Top = 76, Width = 84, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { text, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        return form.ShowDialog(owner) == DialogResult.OK && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
