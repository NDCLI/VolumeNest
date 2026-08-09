using System.Runtime.InteropServices;
using System.Drawing.Drawing2D;

namespace VolumeNest;

public sealed partial class EqualizerForm : Form
{
    private static readonly Color Surface0   = Color.FromArgb(32, 32, 32);
    private static readonly Color Surface1   = Color.FromArgb(40, 40, 40);
    private static readonly Color Surface2   = Color.FromArgb(48, 48, 48);
    private static readonly Color Accent     = Color.FromArgb(0, 103, 192);
    private static readonly Color TextPri    = Color.FromArgb(255, 255, 255);
    private static readonly Color TextSec    = Color.FromArgb(157, 157, 157);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int HotkeyId     = 2;
    private const uint MOD_ALT     = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const int WM_HOTKEY    = 0x0312;

    private readonly string[] _bands = { "31Hz", "63Hz", "125Hz", "250Hz", "500Hz", "1kHz", "2kHz", "4kHz", "8kHz", "16kHz" };
    private readonly int[] _freqs = { 31, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private readonly TrackBar[] _sliders;
    private readonly Label[] _valLabels;

    public EqualizerForm()
    {
        Text            = "Equalizer - VolumeNest";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox     = false;
        MinimizeBox     = true;
        ShowInTaskbar   = true;
        TopMost         = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Surface0;
        Width           = 440;
        Height          = 360;
        Padding         = new Padding(12);

        _sliders = new TrackBar[_bands.Length];
        _valLabels = new Label[_bands.Length];

        BuildUi();
        FormClosing += (s, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotKey(Handle, HotkeyId, MOD_CONTROL | MOD_ALT, (uint)Keys.E);
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            ToggleEq();
        base.WndProc(ref m);
    }

    public void ToggleEq()
    {
        if (Visible) HideEq(); else ShowEq();
    }

    public void ShowEq()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    public void HideEq()
    {
        Hide();
    }

    private bool _isApplyingPreset = false;

    private readonly Dictionary<string, int[]> _presets = new()
    {
        { "Mặc định (Flat)", new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 } },
        { "Bass Boost (Tăng Trầm)", new[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 } },
        { "Treble Boost (Tăng Trong)", new[] { 0, 0, 0, 0, 0, 1, 3, 5, 6, 7 } },
        { "Vocal / Lời nói", new[] { -2, -1, 0, 2, 4, 4, 3, 1, 0, -1 } },
        { "Gaming (Chân thực)", new[] { 4, 3, 1, 0, -1, 2, 4, 3, 2, 1 } },
        { "Nhạc Pop / Rock", new[] { 4, 3, 1, -1, -2, 1, 3, 4, 4, 3 } }
    };

    private ComboBox? _presetCombo;

    private void BuildUi()
    {
        var title = new Label
        {
            Text = "🎛 Equalizer",
            Left = 16, Top = 12, Width = 110, Height = 24,
            Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = TextPri, BackColor = Color.Transparent
        };
        Controls.Add(title);

        _presetCombo = new ComboBox
        {
            Left = 130, Top = 10, Width = 190, Height = 24,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Surface2, ForeColor = TextPri,
            Font = new Font("Segoe UI", 8.5f), FlatStyle = FlatStyle.Flat
        };
        foreach (var p in _presets.Keys) _presetCombo.Items.Add(p);
        _presetCombo.Items.Add("Tùy chỉnh (Custom)");
        _presetCombo.SelectedIndex = 0;
        _presetCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_isApplyingPreset) return;
            if (_presetCombo.SelectedItem is string key && _presets.TryGetValue(key, out var vals))
            {
                ApplyPresetValues(vals);
            }
        };
        Controls.Add(_presetCombo);

        var resetBtn = new Button
        {
            Text = "🔄 Reset", Left = 330, Top = 9, Width = 65, Height = 26,
            FlatStyle = FlatStyle.Flat, ForeColor = TextPri, BackColor = Surface2,
            Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand
        };
        resetBtn.FlatAppearance.BorderSize = 0;
        resetBtn.Click += (_, _) => ResetBands();
        Controls.Add(resetBtn);

        int colW = 38;
        int startX = 16;

        for (int i = 0; i < _bands.Length; i++)
        {
            int x = startX + i * colW;

            var valLbl = new Label
            {
                Text = "0dB", Left = x - 4, Top = 44, Width = colW, Height = 16,
                Font = new Font("Segoe UI", 7.5f), ForeColor = TextSec,
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent
            };
            _valLabels[i] = valLbl;
            Controls.Add(valLbl);

            var slider = new TrackBar
            {
                Orientation = Orientation.Vertical,
                Left = x, Top = 64, Width = colW, Height = 180,
                Minimum = -12, Maximum = 12, Value = 0,
                TickStyle = TickStyle.None, BackColor = Surface0
            };
            int bandIdx = i;
            slider.ValueChanged += (_, _) =>
            {
                int val = slider.Value;
                _valLabels[bandIdx].Text = (val > 0 ? "+" : "") + val + "dB";
                if (!_isApplyingPreset && _presetCombo != null && _presetCombo.SelectedItem?.ToString() != "Tùy chỉnh (Custom)")
                {
                    _presetCombo.SelectedItem = "Tùy chỉnh (Custom)";
                }
                ApplyEqConfig();
            };
            _sliders[i] = slider;
            Controls.Add(slider);

            var bandLbl = new Label
            {
                Text = _bands[i], Left = x - 6, Top = 248, Width = colW + 4, Height = 18,
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold), ForeColor = TextPri,
                TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent
            };
            Controls.Add(bandLbl);
        }

        var hint = new Label
        {
            Text = "Phím tắt mở nhanh: Ctrl + Alt + E",
            Left = 16, Top = 282, Width = 380, Height = 20,
            Font = new Font("Segoe UI", 8f, FontStyle.Italic), ForeColor = TextSec,
            TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent
        };
        Controls.Add(hint);
    }

    private void ApplyPresetValues(int[] values)
    {
        _isApplyingPreset = true;
        for (int i = 0; i < _sliders.Length && i < values.Length; i++)
        {
            _sliders[i].Value = values[i];
            _valLabels[i].Text = (values[i] > 0 ? "+" : "") + values[i] + "dB";
        }
        _isApplyingPreset = false;
        ApplyEqConfig();
    }

    private void ResetBands()
    {
        if (_presetCombo != null) _presetCombo.SelectedIndex = 0;
        ApplyPresetValues(_presets["Mặc định (Flat)"]);
    }

    private bool _warnedNoApo = false;

    private void ApplyEqConfig()
    {
        try
        {
            string eqFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "EqualizerAPO", "config");
            if (!Directory.Exists(eqFolder))
            {
                if (!_warnedNoApo)
                {
                    _warnedNoApo = true;
                    MessageBox.Show("Để Equalizer thay đổi âm thanh thực tế trên Windows, bạn cần cài đặt phần mềm lọc âm thanh miễn phí 'Equalizer APO'.\n\nHệ thống Windows không tích hợp sẵn bộ lọc tần số này trực tiếp.", "Thông báo Equalizer APO", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }

            string configPath = Path.Combine(eqFolder, "volumenest_eq.txt");
            var graphicEq = new List<string>();
            for (int i = 0; i < _freqs.Length; i++)
            {
                graphicEq.Add($"{_freqs[i]} {_sliders[i].Value}");
            }

            string content = "GraphicEQ: " + string.Join("; ", graphicEq);
            File.WriteAllText(configPath, content);

            // Đảm bảo config.txt chính có Include volumenest_eq.txt
            string mainConfig = Path.Combine(eqFolder, "config.txt");
            if (File.Exists(mainConfig))
            {
                string mainTxt = File.ReadAllText(mainConfig);
                if (!mainTxt.Contains("Include: volumenest_eq.txt"))
                {
                    File.AppendAllText(mainConfig, "\nInclude: volumenest_eq.txt\n");
                }
            }
        }
        catch { }
    }
}
