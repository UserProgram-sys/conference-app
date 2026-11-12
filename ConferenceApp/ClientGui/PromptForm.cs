namespace Conference.ClientGui;

/// <summary>Small modal dialog that asks for one or more text values (optionally masked).</summary>
public sealed class PromptForm : Form
{
    private readonly TextBox[] _boxes;

    public string[] Values => _boxes.Select(b => b.Text.Trim()).ToArray();

    public PromptForm(string title, params (string Label, bool Secret)[] fields)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false;
        ClientSize = new Size(330, 25 + fields.Length * 32 + 50);

        _boxes = new TextBox[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            Controls.Add(new Label { Text = fields[i].Label, Left = 10, Top = 25 + i * 32 + 3, Width = 115 });
            _boxes[i] = new TextBox { Left = 130, Top = 25 + i * 32, Width = 185, UseSystemPasswordChar = fields[i].Secret };
            Controls.Add(_boxes[i]);
        }
        int y = 25 + fields.Length * 32 + 8;
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 150, Top = y, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 235, Top = y, Width = 80 };
        Controls.Add(ok); Controls.Add(cancel);
        AcceptButton = ok; CancelButton = cancel;
    }
}
