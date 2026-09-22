using System.Drawing;
using System.Windows.Forms;

namespace MouseRecorder;

/// <summary>Small red STOP window, always on top, vertically centered on the right edge of the primary screen.</summary>
public sealed class StopForm : Form
{
    public event EventHandler? StopClicked;

    public StopForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        Size = new Size(30, 30);
        BackColor = Color.Red;

        var primary = Screen.PrimaryScreen!.Bounds;
        Location = new Point(primary.Right - Width, primary.Top + (primary.Height - Height) / 2);

        var label = new Label
        {
            Dock = DockStyle.Fill,
            Text = "■",
            ForeColor = Color.White,
            BackColor = Color.Red,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Cursor = Cursors.Hand
        };
        label.Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        Controls.Add(label);

        Click += (_, _) => StopClicked?.Invoke(this, EventArgs.Empty);
        Cursor = Cursors.Hand;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }
}
