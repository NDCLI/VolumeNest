using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeNest;

// ── MixerChannel ──────────────────────────────────────────────────────────────
/// <summary>Một dòng trong mixer: volume tổng hoặc một app.</summary>
public sealed class MixerChannel
{
    public required string Name { get; init; }
    public required Func<float> GetVolume { get; init; }      // 0..1
    public required Action<float> SetVolume { get; init; }
    public required Func<bool> GetMute { get; init; }
    public required Action<bool> SetMute { get; init; }
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// PID của process sở hữu session.
    /// null = Volume tổng hoặc System sounds (không hỗ trợ per-app routing).
    /// </summary>
    public uint? ProcessId { get; init; }

    /// <summary>Icon của app (từ exe). null = không lấy được.</summary>
    public Icon? AppIcon { get; init; }
}

// ── DeviceInfo ────────────────────────────────────────────────────────────────
/// <summary>Thông tin thiết bị audio (cho ComboBox).</summary>
public sealed record DeviceInfo(string Id, string Name);

// ── AudioService ──────────────────────────────────────────────────────────────
public sealed class AudioService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;

    // Per-app device routing — null nếu OS không hỗ trợ
    private readonly AudioPolicyConfigService? _policyConfig;
    // System default device switching
    private readonly PolicyConfigService? _systemConfig;

    public AudioService()
    {
        try { _policyConfig = AudioPolicyConfigService.Create(); }
        catch { /* Win10 cũ hoặc không hỗ trợ → tắt per-app routing */ }

        try { _systemConfig = PolicyConfigService.Create(); }
        catch { /* Không hỗ trợ → tắt default device switching */ }
    }

    /// <summary>true nếu per-app device routing được hỗ trợ.</summary>
    public bool SupportsPerAppRouting => _policyConfig is not null;

    private MMDevice Device =>
        _device ??= _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

    public string DeviceName => Device.FriendlyName;

    /// <summary>Gọi khi đổi thiết bị phát mặc định (tai nghe cắm/rút).</summary>
    public void InvalidateDevice()
    {
        _device?.Dispose();
        _device = null;
    }

    // ── Master ────────────────────────────────────────────────────────────────

    public MixerChannel MasterChannel()
    {
        var vol = Device.AudioEndpointVolume;
        return new MixerChannel
        {
            Name      = "Volume tổng",
            GetVolume = () => vol.MasterVolumeLevelScalar,
            SetVolume = v => vol.MasterVolumeLevelScalar = Math.Clamp(v, 0f, 1f),
            GetMute   = () => vol.Mute,
            SetMute   = m => vol.Mute = m,
            // ProcessId = null → không hiện device row
        };
    }

    // ── App sessions ──────────────────────────────────────────────────────────

    /// <summary>
    /// Enum sessions từ TẤT CẢ render devices đang active (không chỉ default).
    /// Gộp các session cùng process thành 1 dòng (Chrome/Edge mở nhiều session).
    /// </summary>
    public List<MixerChannel> AppChannels()
    {
        var groups = new Dictionary<string, List<SimpleAudioVolume>>(StringComparer.OrdinalIgnoreCase);
        var paths  = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var pids   = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        var icons  = new Dictionary<string, Icon?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var allRenderDevices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int d = 0; d < allRenderDevices.Count; d++)
                CollectSessions(allRenderDevices[d], groups, paths, pids, icons);
        }
        catch
        {
            CollectSessions(Device, groups, paths, pids, icons);
        }

        return groups
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g =>
            {
                var ctls = g.Value;
                uint? pid = pids.TryGetValue(g.Key, out var p) ? p : null;
                icons.TryGetValue(g.Key, out var icon);
                return new MixerChannel
                {
                    Name           = g.Key,
                    ExecutablePath = paths[g.Key],
                    ProcessId      = pid,
                    AppIcon        = icon,
                    GetVolume      = () => ctls[0].Volume,
                    SetVolume      = v =>
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

    private static void CollectSessions(
        MMDevice device,
        Dictionary<string, List<SimpleAudioVolume>> groups,
        Dictionary<string, string?> paths,
        Dictionary<string, uint> pids,
        Dictionary<string, Icon?> icons)
    {
        try
        {
            var manager = device.AudioSessionManager;
            manager.RefreshSessions();

            for (int i = 0; i < manager.Sessions.Count; i++)
            {
                var session = manager.Sessions[i];
                if (session.State == AudioSessionState.AudioSessionStateExpired) continue;

                var (name, path, pid, icon) = Describe(session);
                if (!groups.TryGetValue(name, out var list))
                {
                    groups[name] = list = new List<SimpleAudioVolume>();
                    paths[name]  = path;
                    if (pid.HasValue) pids[name] = pid.Value;
                    if (icon != null) icons[name] = icon;
                }
                list.Add(session.SimpleAudioVolume);
            }
        }
        catch { }
    }

    // ── Device enumeration ────────────────────────────────────────────────────

    /// <summary>Liệt kê tất cả thiết bị output đang hoạt động.</summary>
    public List<DeviceInfo> GetRenderDevices()
        => GetDevices(DataFlow.Render);

    /// <summary>Liệt kê tất cả thiết bị input đang hoạt động.</summary>
    public List<DeviceInfo> GetCaptureDevices()
        => GetDevices(DataFlow.Capture);

    private List<DeviceInfo> GetDevices(DataFlow flow)
    {
        try
        {
            var result = new List<DeviceInfo>();
            var collection = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            for (int i = 0; i < collection.Count; i++)
            {
                var dev = collection[i];
                result.Add(new DeviceInfo(dev.ID, dev.FriendlyName));
            }
            return result;
        }
        catch
        {
            return new List<DeviceInfo>();
        }
    }

    // ── System default device ─────────────────────────────────────────────────

    /// <summary>Chuyển đầu ra mặc định hệ thống. Trả true nếu thành công.</summary>
    public bool SetDefaultRenderDevice(string deviceId)
    {
        try { _systemConfig?.SetDefaultRenderDevice(deviceId); return true; }
        catch { return false; }
    }

    /// <summary>Lấy ID thiết bị đầu ra mặc định hiện tại.</summary>
    public string GetDefaultRenderDeviceId()
    {
        try { return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { return ""; }
    }

    // ── Per-app device routing ────────────────────────────────────────────────

    /// <summary>Gán output device cho process. null = system default.</summary>
    public void SetAppOutput(uint pid, string? deviceId)
    {
        try { _policyConfig?.SetAppOutput(pid, deviceId); } catch { }
    }

    /// <summary>Gán input device cho process. null = system default.</summary>
    public void SetAppInput(uint pid, string? deviceId)
    {
        try { _policyConfig?.SetAppInput(pid, deviceId); } catch { }
    }

    /// <summary>Lấy output device đang được gán cho process. null = system default.</summary>
    public string? GetAppOutput(uint pid)
    {
        try { return _policyConfig?.GetAppOutput(pid); } catch { return null; }
    }

    /// <summary>Lấy input device đang được gán cho process. null = system default.</summary>
    public string? GetAppInput(uint pid)
    {
        try { return _policyConfig?.GetAppInput(pid); } catch { return null; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (string Name, string? Path, uint? Pid, Icon? AppIcon) Describe(AudioSessionControl session)
    {
        try
        {
            uint pid = session.GetProcessID;
            if (pid == 0 || session.IsSystemSoundsSession)
                return ("System sounds", null, null, null);

            using var proc = Process.GetProcessById((int)pid);
            string? path = null;
            Icon? icon = null;
            try
            {
                path = proc.MainModule?.FileName;
                if (path != null) icon = Icon.ExtractAssociatedIcon(path);
            }
            catch { /* thiếu quyền */ }

            return (proc.ProcessName, path, pid, icon);
        }
        catch
        {
            return (string.IsNullOrWhiteSpace(session.DisplayName) ? "Unknown" : session.DisplayName, null, null, null);
        }
    }

    public void Dispose()
    {
        _systemConfig?.Dispose();
        _policyConfig?.Dispose();
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
