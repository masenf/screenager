using Screenager.Native;

namespace Screenager.Ui;

public enum OverrideAction { Cancel, Grant, Revoke }

/// <summary>
/// PIN-gated parent dialog: grant bonus minutes for the rest of the day, or revoke all extra
/// time already granted. Shows how much has been granted so far.
/// </summary>
public sealed class OverrideDialog : Form
{
    private readonly string _pin;
    private readonly TextBox _pinBox;
    private readonly NumericUpDown _minutes;
    private readonly Label _error;

    public OverrideAction Action { get; private set; } = OverrideAction.Cancel;
    public int GrantedMinutes { get; private set; }

    public OverrideDialog(string pin, int grantedSecondsToday)
    {
        _pin = pin;

        Text = "Screenager — Parent Override";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        Font = new Font("Segoe UI", 9.75f);
        ClientSize = new Size(360, 210);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        int grantedMin = grantedSecondsToday / 60;
        var granted = new Label
        {
            Text = grantedMin > 0
                ? $"Extra time granted today: {grantedMin} min"
                : "No extra time granted today.",
            ForeColor = grantedMin > 0 ? Color.DarkGreen : SystemColors.GrayText,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold),
        };
        layout.Controls.Add(granted, 0, 0);
        layout.SetColumnSpan(granted, 2);

        layout.Controls.Add(MakeLabel("Parent PIN:"), 0, 1);
        _pinBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_pinBox, 1, 1);

        layout.Controls.Add(MakeLabel("Add minutes:"), 0, 2);
        _minutes = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 1440, Value = 30, Margin = new Padding(0, 8, 0, 8) };
        layout.Controls.Add(_minutes, 1, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        var cancel = new Button { Text = "Cancel", Width = 80, DialogResult = DialogResult.Cancel };
        var revoke = new Button { Text = "Revoke", Width = 80, Enabled = grantedMin > 0 };
        var grant = new Button { Text = "Grant", Width = 80 };
        grant.Click += (_, _) => Submit(OverrideAction.Grant);
        revoke.Click += (_, _) => Submit(OverrideAction.Revoke);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(revoke);
        buttons.Controls.Add(grant);
        layout.Controls.Add(buttons, 0, 3);
        layout.SetColumnSpan(buttons, 2);

        _error = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleLeft };

        // Add the docked-Bottom control first, then the Fill control, so Fill takes the remaining space.
        Controls.Add(_error);
        Controls.Add(layout);

        AcceptButton = grant;
        CancelButton = cancel;
        ActiveControl = _pinBox;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // The dialog may open while a topmost warning (or game) holds focus; force it forward
        // and put the cursor in the PIN field so the parent can type immediately.
        NativeMethods.ForceForeground(Handle);
        _pinBox.Focus();
        _pinBox.Select();
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false,
    };

    private void Submit(OverrideAction action)
    {
        if (string.IsNullOrEmpty(_pin) || _pinBox.Text != _pin)
        {
            _error.Text = "Incorrect PIN.";
            _pinBox.SelectAll();
            _pinBox.Focus();
            return;
        }
        Action = action;
        GrantedMinutes = action == OverrideAction.Grant ? (int)_minutes.Value : 0;
        DialogResult = DialogResult.OK;
        Close();
    }
}
