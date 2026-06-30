namespace Screenager.Ui;

/// <summary>
/// PIN-gated parent dialog to grant bonus minutes for the rest of the day.
/// </summary>
public sealed class OverrideDialog : Form
{
    private readonly string _pin;
    private readonly TextBox _pinBox;
    private readonly NumericUpDown _minutes;
    private readonly Label _error;

    public int GrantedMinutes { get; private set; }

    public OverrideDialog(string pin)
    {
        _pin = pin;

        Text = "Screenager — Parent Override";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ClientSize = new Size(320, 200);
        Font = new Font("Segoe UI", 10f);

        Controls.Add(new Label { Text = "Parent PIN:", Left = 16, Top = 18, Width = 120 });
        _pinBox = new TextBox { Left = 140, Top = 15, Width = 150, UseSystemPasswordChar = true };
        Controls.Add(_pinBox);

        Controls.Add(new Label { Text = "Add minutes:", Left = 16, Top = 58, Width = 120 });
        _minutes = new NumericUpDown { Left = 140, Top = 55, Width = 150, Minimum = 1, Maximum = 1440, Value = 30 };
        Controls.Add(_minutes);

        _error = new Label { Left = 16, Top = 92, Width = 288, ForeColor = Color.Firebrick };
        Controls.Add(_error);

        var ok = new Button { Text = "Grant", Left = 140, Top = 130, Width = 70, DialogResult = DialogResult.None };
        ok.Click += OnGrant;
        Controls.Add(ok);

        var cancel = new Button { Text = "Cancel", Left = 220, Top = 130, Width = 70, DialogResult = DialogResult.Cancel };
        Controls.Add(cancel);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void OnGrant(object? sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(_pin) || _pinBox.Text != _pin)
        {
            _error.Text = "Incorrect PIN.";
            _pinBox.SelectAll();
            _pinBox.Focus();
            return;
        }
        GrantedMinutes = (int)_minutes.Value;
        DialogResult = DialogResult.OK;
        Close();
    }
}
