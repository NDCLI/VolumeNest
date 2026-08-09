# VolumeNest — Tool tray chỉnh volume mixer (C# .NET + NAudio)

<aside>
🎯

**Mục tiêu:** một app nhỏ nằm ở khay hệ thống (tray). Click chuột trái → hiện flyout có slider cho **từng ứng dụng** (Discord, League of Legends, Edge…) + volume tổng, không cần vào Settings → Sound → Volume mixer.

</aside>

## 1. Kiến trúc

| Thành phần | Công nghệ | Vai trò |
| --- | --- | --- |
| Ngôn ngữ / runtime | C# 12, .NET 8 (`net8.0-windows`) | Native trên Windows, exe gọn, không cần Python |
| Điều khiển âm lượng | **NAudio.CoreAudioApi** (wrap WASAPI) | `SimpleAudioVolume` cho từng session, `AudioEndpointVolume` cho volume tổng |
| Tray icon | `NotifyIcon`  • `ContextMenuStrip` (WinForms) | Icon khay, menu phải, click trái mở flyout |
| Flyout slider | `Form` borderless + `TrackBar` | Cửa sổ dark-mode góc phải dưới, mất focus là tự ẩn |
| Đóng gói | `dotnet publish` single-file | 1 file `VolumeNest.exe` |

## 2. Tạo project

```powershell
dotnet new winforms -n VolumeNest
cd VolumeNest
dotnet add package NAudio
```

`VolumeNest.csproj` — sửa lại cho gọn:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
  </ItemGroup>
</Project>
```

Xóa `Form1.cs`, `Form1.Designer.cs` — toàn bộ UI dụng bằng code.

## 3. `Program.cs`

```csharp
using System.Diagnostics;

namespace VolumeNest;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // chỉ cho phép 1 instance
        using var mutex = new Mutex(true, "VolumeNest.SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        using var tray = new TrayApp();
        Application.Run();
    }
}
```

## 4. `AudioService.cs` — lớp bọc NAudio

```csharp
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace VolumeNest;

/// <summary>Một dòng trong mixer: volume tổng hoặc một app.</summary>
public sealed class MixerChannel
{
    public required string Name { get; init; }
    public required Func<float> GetVolume { get; init; }      // 0..1
    public required Action<float> SetVolume { get; init; }
    public required Func<bool> GetMute { get; init; }
    public required Action<bool> SetMute { get; init; }
    public string? ExecutablePath { get; init; }
}

public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    private MMDevice Device =>
        _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    public string DeviceName => Device.FriendlyName;

    /// <summary>Gọi khi đổi thiết bị phát mặc định (tai nghe cắm/rút).</summary>
    public void InvalidateDevice()
    {
        _device?.Dispose();
        _device = null;
    }

    public MixerChannel MasterChannel()
    {
        var vol = Device.AudioEndpointVolume;
        return new MixerChannel
        {
            Name = "Volume tổng",
            GetVolume = () => vol.MasterVolumeLevelScalar,
            SetVolume = v => vol.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f),
            GetMute = () => vol.Mute,
            SetMute = m => vol.Mute = m,
        };
    }

    /// <summary>Gộp các session cùng process thành 1 dòng (Chrome/Edge mở nhiều session).</summary>
    public List<MixerChannel> AppChannels()
    {
        var manager = Device.AudioSessionManager;
        manager.RefreshSessions();

        var groups = new Dictionary<string, List<SimpleAudioVolume>>(StringComparer.OrdinalIgnoreCase);
        var paths = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < manager.Sessions.Count; i++)
        {
            var session = manager.Sessions[i];
            if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

            var (name, path) = Describe(session);
            if (!groups.TryGetValue(name, out var list))
            {
                groups[name] = list = new List<SimpleAudioVolume>();
                paths[name] = path;
            }
            list.Add(session.SimpleAudioVolume);
        }

        return groups
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var ctls = g.Value;
                return new MixerChannel
                {
                    Name = g.Key,
                    ExecutablePath = paths[g.Key],
                    GetVolume = () => ctls[0].Volume,
                    SetVolume = v =>
                    {
                        v = Math.Clamp(v, 0f, 1f);
                        foreach (var c in ctls) c.Volume = v;
                    },
                    GetMute = () => ctls[0].Mute,
                    SetMute = m => { foreach (var c in ctls) c.Mute = m; },
                };
            })
            .ToList();
    }

    private static (string Name, string? Path) Describe(AudioSessionControl session)
    {
        try
        {
            uint pid = session.GetProcessID;
            if (pid == 0 || session.IsSystemSoundsSession)
                return ("System sounds", null);

            using var proc = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = proc.MainModule?.FileName; } catch { /* thiếu quyền */ }
            string friendly = proc.MainWindowTitle.Length is > 0 and < 40
                ? proc.ProcessName
                : proc.ProcessName;
            return (friendly, path);
        }
        catch
        {
            return (string.IsNullOrWhiteSpace(session.DisplayName) ? "Unknown" : session.DisplayName, null);
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
```

## 5. `MixerForm.cs` — flyout slider

```csharp
using System.Drawing;

namespace VolumeNest;

public sealed class MixerForm : Form
{
    private static readonly Color Bg = Color.FromArgb(32, 32, 32);
    private static readonly Color Fg = Color.FromArgb(235, 235, 235);
    private static readonly TimeSpan UserHold = TimeSpan.FromMilliseconds(1200);

    private readonly AudioService _audio;
    private readonly Panel _body;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly List<RowUi> _rows = new();

    private sealed class RowUi
    {
        public required MixerChannel Channel { get; init; }
        public required TrackBar Bar { get; init; }
        public required Label Value { get; init; }
        public required Button MuteBtn { get; init; }
        public DateTime Touched;
    }

    public MixerForm(AudioService audio)
    {
        _audio = audio;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Bg;
        Padding = new Padding(12);
        Width = 420;
        KeyPreview = true;

        _body = new Panel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Bg };
        Controls.Add(_body);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => SyncValues();

        Deactivate += (_, _) => HideFlyout();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) HideFlyout(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            cp.ExStyle |= 0x00000080;      // WS_EX_TOOLWINDOW: không hiện trong Alt+Tab
            return cp;
        }
    }

    public void ToggleFlyout()
    {
        if (Visible) HideFlyout();
        else ShowFlyout();
    }

    public void ShowFlyout()
    {
        Rebuild();
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - 12, area.Bottom - Height - 12);
        Show();
        Activate();
        _timer.Start();
    }

    public void HideFlyout()
    {
        _timer.Stop();
        Hide();
    }

    private void Rebuild()
    {
        _body.SuspendLayout();
        _body.Controls.Clear();
        _rows.Clear();

        int y = 0;
        AddHeader(_audio.DeviceName, ref y);
        AddRow(_audio.MasterChannel(), ref y, bold: true);

        _body.Controls.Add(new Panel
        {
            Left = 4, Top = y + 4, Width = Width - 40, Height = 1,
            BackColor = Color.FromArgb(70, 70, 70)
        });
        y += 14;

        var apps = _audio.AppChannels();
        if (apps.Count == 0) AddHeader("Không có app nào đang phát âm thanh", ref y);
        foreach (var ch in apps) AddRow(ch, ref y);

        Height = y + 24;
        _body.ResumeLayout();
    }

    private void AddHeader(string text, ref int y)
    {
        _body.Controls.Add(new Label
        {
            Text = text, Left = 4, Top = y, Width = Width - 40, Height = 18,
            ForeColor = Color.FromArgb(150, 150, 150), BackColor = Bg,
            Font = new Font("Segoe UI", 8.5f)
        });
        y += 22;
    }

    private void AddRow(MixerChannel ch, ref int y, bool bold = false)
    {
        var label = new Label
        {
            Text = ch.Name, Left = 4, Top = y + 4, Width = 150, Height = 20,
            ForeColor = Fg, BackColor = Bg,
            Font = new Font("Segoe UI", 9.5f, bold ? FontStyle.Bold : FontStyle.Regular)
        };

        var mute = new Button
        {
            Left = 158, Top = y + 2, Width = 30, Height = 24,
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 45, 45),
            ForeColor = Fg, Font = new Font("Segoe UI Emoji", 9f), TabStop = false
        };
        mute.FlatAppearance.BorderSize = 0;

        var bar = new TrackBar
        {
            Left = 194, Top = y, Width = 170, Height = 30,
            Minimum = 0, Maximum = 100, TickStyle = TickStyle.None,
            BackColor = Bg
        };

        var value = new Label
        {
            Left = 368, Top = y + 4, Width = 34, Height = 20,
            ForeColor = Fg, BackColor = Bg, Font = new Font("Segoe UI", 9f),
            TextAlign = ContentAlignment.MiddleRight
        };

        var row = new RowUi { Channel = ch, Bar = bar, Value = value, MuteBtn = mute };

        bar.Value = ToPercent(SafeGet(ch.GetVolume));
        value.Text = bar.Value + "%";
        UpdateMuteIcon(row);

        bar.Scroll += (_, _) =>
        {
            row.Touched = DateTime.UtcNow;
            value.Text = bar.Value + "%";
            try { ch.SetVolume(bar.Value / 100f); } catch { }
        };

        mute.Click += (_, _) =>
        {
            try { ch.SetMute(!ch.GetMute()); } catch { }
            UpdateMuteIcon(row);
        };

        _body.Controls.AddRange(new Control[] { label, mute, bar, value });
        _rows.Add(row);
        y += 34;
    }

    private void SyncValues()
    {
        foreach (var row in _rows)
        {
            if (DateTime.UtcNow - row.Touched < UserHold) continue;
            int real = ToPercent(SafeGet(row.Channel.GetVolume));
            if (Math.Abs(real - row.Bar.Value) > 1)
            {
                row.Bar.Value = real;
                row.Value.Text = real + "%";
            }
            UpdateMuteIcon(row);
        }
    }

    private static void UpdateMuteIcon(RowUi row)
    {
        try { row.MuteBtn.Text = row.Channel.GetMute() ? "🔇" : "🔊"; } catch { }
    }

    private static float SafeGet(Func<float> getter)
    {
        try { return getter(); } catch { return 0f; }
    }

    private static int ToPercent(float v) => Math.Clamp((int)Math.Round(v * 100f), 0, 100);
}
```

## 6. `TrayApp.cs` — icon khay + menu

```csharp
using System.Drawing;
using System.Drawing.Drawing2D;

namespace VolumeNest;

public sealed class TrayApp : IDisposable
{
    private readonly AudioService _audio = new();
    private readonly NotifyIcon _icon;
    private readonly MixerForm _form;

    public TrayApp()
    {
        _form = new MixerForm(_audio);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Mở mixer", null, (_, _) => _form.ShowFlyout());
        menu.Items.Add(new ToolStripSeparator());
        foreach (int pct in new[] { 25, 50, 100 })
        {
            int p = pct;
            menu.Items.Add($"Volume tổng {p}%", null, (_, _) =>
                _audio.MasterChannel().SetVolume(p / 100f));
        }
        menu.Items.Add("Mute / unmute tổng", null, (_, _) =>
        {
            var m = _audio.MasterChannel();
            m.SetMute(!m.GetMute());
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thiết bị phát (Windows)", null, (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ms-settings:sound") { UseShellExecute = true }));
        menu.Items.Add("Thoát", null, (_, _) => ExitApp());

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "VolumeNest",
            Visible = true,
            ContextMenuStrip = menu
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _form.ToggleFlyout();
            if (e.Button == MouseButtons.Middle)
            {
                var m = _audio.MasterChannel();
                m.SetMute(!m.GetMute());
            }
        };
    }

    private static Icon BuildIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.White);
            g.FillPolygon(brush, new[]
            {
                new Point(5, 12), new Point(12, 12), new Point(19, 4),
                new Point(19, 28), new Point(12, 20), new Point(5, 20)
            });
            using var pen = new Pen(Color.White, 2.5f);
            g.DrawArc(pen, 18, 7, 11, 18, -60, 120);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private void ExitApp()
    {
        _icon.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        _icon.Dispose();
        _form.Dispose();
        _audio.Dispose();
    }
}
```

## 7. Build & đóng gói

```powershell
dotnet run                    # chạy thử

# 1 file exe, không cần cài .NET trên máy đích
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

# hoặc nhẹ hơn nhiều (~200 KB) nếu máy đã có .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
```

**Tự khởi động cùng Windows:** `Win + R` → `shell:startup` → tạo shortcut tới `VolumeNest.exe`.

## 8. Nâng cấp tuỳ chọn

**Hotkey toàn cục** (`Ctrl + Alt + V` mở mixer) — thêm vào `MixerForm`:

```csharp
using System.Runtime.InteropServices;

[LibraryImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

protected override void OnHandleCreated(EventArgs e)
{
    base.OnHandleCreated(e);
    RegisterHotKey(Handle, 1, 0x0002 | 0x0001, (uint)Keys.V); // MOD_CONTROL | MOD_ALT
}

protected override void WndProc(ref Message m)
{
    if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) ToggleFlyout();  // WM_HOTKEY
    base.WndProc(ref m);
}
```

(nhậy `partial class MixerForm` khi dùng `LibraryImport`)

**Icon thật của từng app** trong flyout: đã có `MixerChannel.ExecutablePath`, chỉ cần

```csharp
var img = Icon.ExtractAssociatedIcon(ch.ExecutablePath!)?.ToBitmap();
```

**Cuộn chuột trên tray icon để tăng/giảm volume tổng**: `NotifyIcon` không bắn `MouseWheel`; cần tự đăng ký `Shell_NotifyIcon` qua P/Invoke và bắt `WM_MOUSEWHEEL` — hoặc đơn giản là cuộn trên slider trong flyout.

**Đổi output device cho từng app** (phần dưới cùng trong ảnh): Windows không có API công khai. Hai cách:

1. Interop với COM interface không tài liệu `IPolicyConfig` (CLSID `870af99c-171d-4f9e-af0d-e63df40c2bc9`) — hoạt động nhưng có thể vỡ khi Windows update.
2. Gọi `SoundVolumeView.exe` (NirSoft) — ổn định, dễ làm:

```csharp
Process.Start("SoundVolumeView.exe",
    "/SetAppDefault \"Headphones (Creative Stage Air V2)\" all \"League of Legends.exe\"");
```

**Ghi nhớ preset**: lưu JSON `{ "League of Legends": 39 }` vào `%APPDATA%\VolumeNest\presets.json`, áp lại khi app xuất hiện trong danh sách session (dùng `AudioSessionManager.OnSessionCreated`).

## 9. Lưu ý kỹ thuật

- App chỉ hiện trong mixer khi **đang mở audio stream** — app im lặng lâu sẽ biến mất (đúng hành vi của Windows).
- Volume mỗi app là **tương đối với volume tổng** (LoL 39% nghĩa là 39% của mức tổng).
- NAudio đã tự xử lý COM apartment, nhưng mọi lệnh âm thanh nên gọi trên **UI thread** để tránh lỗi marshalling.
- Khi cắm/rút tai nghe, thiết bị mặc định đổi → gọi `InvalidateDevice()` (có thể hook `IMMNotificationClient` để tự động).
- Không cần quyền admin.

<aside>
💡

Muốn dùng ngay mà không cần build: **EarTrumpet** (open-source, C#/WinUI, có trên Microsoft Store) làm đúng việc này. Bản tự viết ở trên đáng làm khi bạn muốn preset riêng, hotkey riêng hoặc tự động đổi device.

</aside>