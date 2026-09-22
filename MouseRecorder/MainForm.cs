using System.Drawing;
using System.Windows.Forms;

namespace MouseRecorder;

public sealed class MainForm : Form
{
    private readonly Recorder _recorder = new();
    private readonly Player _player = new();
    private HotkeyManager? _hotkeyManager;

    private StopForm? _stopForm;
    private CountdownForm? _countdownForm;
    private OverlayForm? _overlayForm;

    private Recording? _recording;
    private AppState _state = AppState.Idle;

    // --- Kontrolki ---
    private Button _btnRecord = null!;
    private Button _btnNewRecording = null!;
    private Button _btnPlay = null!;

    private RadioButton _speedX1 = null!;
    private RadioButton _speedX2 = null!;
    private RadioButton _speedX3 = null!;
    private RadioButton _speedX5 = null!;
    private RadioButton _speedX10 = null!;

    private RadioButton _repeatCountRadio = null!;
    private RadioButton _repeatInfiniteRadio = null!;
    private NumericUpDown _repeatCountValue = null!;

    private Label _lblInfo = null!;

    public MainForm()
    {
        BuildUi();
        UpdateUiState();
    }

    private void BuildUi()
    {
        Text = "MouseRecorder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 430);

        _btnRecord = new Button { Text = "Nagrywaj", Location = new Point(20, 20), Size = new Size(180, 36) };
        _btnRecord.Click += (_, _) => BeginRecordingFlow(discardExisting: false);

        _btnNewRecording = new Button { Text = "Nowe nagranie", Location = new Point(220, 20), Size = new Size(180, 36) };
        _btnNewRecording.Click += (_, _) => BeginRecordingFlow(discardExisting: true);

        _btnPlay = new Button { Text = "Odtwórz", Location = new Point(20, 66), Size = new Size(380, 36) };
        _btnPlay.Click += (_, _) => StartPlayback();

        var speedGroup = new GroupBox { Text = "Prędkość", Location = new Point(20, 116), Size = new Size(380, 56) };
        _speedX1 = new RadioButton { Text = "x1", Location = new Point(10, 24), AutoSize = true, Checked = true };
        _speedX2 = new RadioButton { Text = "x2", Location = new Point(80, 24), AutoSize = true };
        _speedX3 = new RadioButton { Text = "x3", Location = new Point(150, 24), AutoSize = true };
        _speedX5 = new RadioButton { Text = "x5", Location = new Point(220, 24), AutoSize = true };
        _speedX10 = new RadioButton { Text = "x10", Location = new Point(290, 24), AutoSize = true };
        speedGroup.Controls.AddRange(new Control[] { _speedX1, _speedX2, _speedX3, _speedX5, _speedX10 });

        var repeatGroup = new GroupBox { Text = "Powtórzenia", Location = new Point(20, 184), Size = new Size(380, 90) };
        _repeatCountRadio = new RadioButton { Text = "Liczba razy", Location = new Point(10, 26), AutoSize = true, Checked = true };
        _repeatCountRadio.CheckedChanged += (_, _) => _repeatCountValue.Enabled = _repeatCountRadio.Checked && _state != AppState.Playing;
        _repeatCountValue = new NumericUpDown { Location = new Point(130, 24), Size = new Size(70, 23), Minimum = 1, Maximum = 100000, Value = 1 };
        _repeatInfiniteRadio = new RadioButton { Text = "W nieskończoność", Location = new Point(10, 56), AutoSize = true };
        repeatGroup.Controls.AddRange(new Control[] { _repeatCountRadio, _repeatCountValue, _repeatInfiniteRadio });

        _lblInfo = new Label
        {
            Text = "Brak nagrania",
            Location = new Point(20, 286),
            Size = new Size(380, 40),
            Font = new Font(Font.FontFamily, 10f)
        };

        var lblHotkeys = new Label
        {
            Text = "F8 – odtwórz, F9 – zatrzymaj",
            Location = new Point(20, 336),
            Size = new Size(380, 24),
            ForeColor = Color.DimGray
        };

        Controls.AddRange(new Control[]
        {
            _btnRecord, _btnNewRecording, _btnPlay, speedGroup, repeatGroup, _lblInfo, lblHotkeys
        });
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _hotkeyManager = new HotkeyManager(Handle);
        var failed = _hotkeyManager.RegisterAll();
        if (failed.Count > 0)
        {
            MessageBox.Show(
                this,
                $"Nie udało się zarejestrować skrótu/skrótów: {string.Join(", ", failed)}.\n" +
                "Prawdopodobnie są już używane przez inny program. Odtwarzanie/zatrzymywanie klawiszami F8/F9 może nie działać.",
                "Błąd rejestracji skrótu klawiszowego",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            int id = m.WParam.ToInt32();
            if (id == HotkeyManager.HotkeyIdPlay)
            {
                TryStartPlaybackViaHotkey();
            }
            else if (id == HotkeyManager.HotkeyIdStop)
            {
                TryStopPlaybackViaHotkey();
            }
        }
        base.WndProc(ref m);
    }

    // ----------------- Nagrywanie -----------------

    private void BeginRecordingFlow(bool discardExisting)
    {
        if (_state != AppState.Idle) return;
        if (discardExisting || _recording == null)
        {
            _recording = null;
        }

        _state = AppState.Countdown;
        Hide();

        _stopForm = new StopForm();
        _stopForm.StopClicked += StopForm_Clicked;
        _stopForm.Show();

        _countdownForm = new CountdownForm();
        _countdownForm.CountdownFinished += CountdownForm_Finished;
        _countdownForm.StartCountdown();
    }

    private void CountdownForm_Finished(object? sender, EventArgs e)
    {
        _countdownForm?.Hide();

        _state = AppState.Recording;
        if (_stopForm != null)
        {
            _recorder.ExcludedRegion = _stopForm.Bounds;
        }
        _recorder.Start();
    }

    private void StopForm_Clicked(object? sender, EventArgs e)
    {
        switch (_state)
        {
            case AppState.Countdown:
                _countdownForm?.CancelCountdown();
                CleanupRecordingWindows();
                _state = AppState.Idle;
                ShowMainForm();
                break;

            case AppState.Recording:
                _recording = _recorder.Stop();
                CleanupRecordingWindows();
                _state = AppState.Idle;
                ShowMainForm();
                break;
        }
    }

    private void CleanupRecordingWindows()
    {
        if (_stopForm != null)
        {
            _stopForm.StopClicked -= StopForm_Clicked;
            _stopForm.Close();
            _stopForm.Dispose();
            _stopForm = null;
        }
        if (_countdownForm != null)
        {
            _countdownForm.CountdownFinished -= CountdownForm_Finished;
            _countdownForm.Close();
            _countdownForm.Dispose();
            _countdownForm = null;
        }
    }

    // ----------------- Odtwarzanie -----------------

    private void TryStartPlaybackViaHotkey()
    {
        if (_state == AppState.Idle && _recording is { Events.Count: > 0 })
        {
            StartPlayback();
        }
    }

    private void TryStopPlaybackViaHotkey()
    {
        if (_state == AppState.Playing)
        {
            _player.Stop();
        }
    }

    private void StartPlayback()
    {
        if (_state != AppState.Idle || _recording is not { Events.Count: > 0 }) return;

        _state = AppState.Playing;
        UpdateUiState();
        Hide();

        _overlayForm = new OverlayForm(_recording, _player.Progress);
        _overlayForm.Show();

        int speed = GetSelectedSpeedDivisor();
        int repeat = GetSelectedRepeatCount();

        _player.Play(_recording, speed, repeat, OnPlaybackFinished);
    }

    private void OnPlaybackFinished()
    {
        if (InvokeRequired)
        {
            BeginInvoke(OnPlaybackFinished);
            return;
        }

        if (_overlayForm != null)
        {
            _overlayForm.Close();
            _overlayForm.Dispose();
            _overlayForm = null;
        }

        _state = AppState.Idle;
        ShowMainForm();
    }

    private int GetSelectedSpeedDivisor()
    {
        if (_speedX2.Checked) return 2;
        if (_speedX3.Checked) return 3;
        if (_speedX5.Checked) return 5;
        if (_speedX10.Checked) return 10;
        return 1;
    }

    private int GetSelectedRepeatCount()
    {
        if (_repeatInfiniteRadio.Checked) return 0;
        return (int)_repeatCountValue.Value;
    }

    // ----------------- Wspólne -----------------

    private void ShowMainForm()
    {
        UpdateUiState();
        Show();
        BringToFront();
        Activate();
    }

    private void UpdateUiState()
    {
        bool idle = _state == AppState.Idle;
        bool hasRecording = _recording is { Events.Count: > 0 };

        _btnRecord.Enabled = idle && !hasRecording;
        _btnNewRecording.Enabled = idle;
        _btnPlay.Enabled = idle && hasRecording;

        bool speedEditable = _state != AppState.Playing;
        _speedX1.Enabled = speedEditable;
        _speedX2.Enabled = speedEditable;
        _speedX3.Enabled = speedEditable;
        _speedX5.Enabled = speedEditable;
        _speedX10.Enabled = speedEditable;

        _repeatCountRadio.Enabled = speedEditable;
        _repeatInfiniteRadio.Enabled = speedEditable;
        _repeatCountValue.Enabled = speedEditable && _repeatCountRadio.Checked;

        if (!hasRecording)
        {
            _lblInfo.Text = "Brak nagrania";
        }
        else
        {
            var duration = TimeSpan.FromMilliseconds(_recording!.DurationMs);
            _lblInfo.Text = $"Liczba zdarzeń: {_recording.Events.Count}\nCzas trwania: {duration:mm\\:ss\\.fff}";
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _player.Stop();
        _hotkeyManager?.Dispose();
        base.OnFormClosing(e);
    }
}
