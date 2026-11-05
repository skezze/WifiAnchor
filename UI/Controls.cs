using WifiBssidTool.Localization;

namespace WifiBssidTool.UI;

internal sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}

internal static class InputDialog
{
    /// <summary>Небольшой диалог с одной строкой ввода. null — отмена.</summary>
    public static string? Show(
        IWin32Window owner, string title, string prompt, string initial = "")
    {
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            AutoScaleMode = AutoScaleMode.Dpi,
            ClientSize = new Size(420, 130)
        };

        var label = new Label
        {
            Text = prompt,
            Left = 12,
            Top = 12,
            Width = 396,
            Height = 36
        };

        var box = new TextBox { Left = 12, Top = 54, Width = 396, Text = initial };
        box.SelectAll();

        var ok = new Button
        {
            Text = Loc.T("dlg.ok"),
            DialogResult = DialogResult.OK,
            Left = 232,
            Top = 90,
            Width = 84
        };

        var cancel = new Button
        {
            Text = Loc.T("dlg.cancel"),
            DialogResult = DialogResult.Cancel,
            Left = 324,
            Top = 90,
            Width = 84
        };

        form.Controls.AddRange(new Control[] { label, box, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
    }
}
