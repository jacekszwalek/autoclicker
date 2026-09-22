using System.Drawing;
using System.Windows.Forms;

namespace MouseRecorder;

/// <summary>Półprzezroczyste, pełnoekranowe okno z dużym odliczaniem 3-2-1. Nie przechwytuje kliknięć.</summary>
public sealed class CountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Label _label;
    private int _remaining = 3;

    public event EventHandler? CountdownFinished;

    public CountdownForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        Opacity = 0.35;

        _label = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 160f, FontStyle.Bold),
            Text = _remaining.ToString()
        };
        Controls.Add(_label);

        _timer.Tick += Timer_Tick;
    }

    public void StartCountdown()
    {
        _remaining = 3;
        _label.Text = _remaining.ToString();
        Show();
        _timer.Start();
    }

    /// <summary>Anuluje odliczanie bez wywołania CountdownFinished (np. po kliknięciu STOP).</summary>
    public void CancelCountdown()
    {
        _timer.Stop();
        Hide();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            _timer.Stop();
            CountdownFinished?.Invoke(this, EventArgs.Empty);
            return;
        }
        _label.Text = _remaining.ToString();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
