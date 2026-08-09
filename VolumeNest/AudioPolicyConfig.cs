using System.Runtime.InteropServices;

namespace VolumeNest;

// ── Raw vtable interop cho IAudioPolicyConfig (Win11 21H2+) ──────────────────
//
// .NET 8 KHÔNG hỗ trợ InterfaceIsIInspectable marshal.
// → Phải gọi vtable thủ công qua Marshal.GetDelegateForFunctionPointer.
//
// vtable layout (IID: AB3D4648-E242-459F-B02F-541C70306324):
//   slots 0-2:  IUnknown  (QueryInterface, AddRef, Release)
//   slots 3-5:  IInspectable (GetIids, GetRuntimeClassName, GetTrustLevel)
//   slots 6-24: 19 internal WinRT methods (không dùng)
//   slot 25:    SetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, IntPtr hstring)
//   slot 26:    GetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, out IntPtr hstring)
//   slot 27:    ClearAllPersistedApplicationDefaultEndpoints()

/// <summary>
/// Gán/lấy thiết bị audio mặc định cho từng ứng dụng theo PID.
/// Chỉ hỗ trợ Windows 11 build ≥ 21390 (21H2+).
/// Dùng raw COM vtable calls vì .NET 8 không marshal IInspectable.
/// </summary>
public sealed class AudioPolicyConfigService : IDisposable
{
    private const int MinBuild = 21390;
    private const string WinRtClass = "Windows.Media.Internal.AudioPolicyConfig";

    // IID cho Win11 21H2+ (build ≥ 21390)
    private static readonly Guid IID = new("ab3d4648-e242-459f-b02f-541c70306324");

    // vtable slot indices
    private const int VtSlot_Release = 2;
    private const int VtSlot_SetPersistedDefault = 25;   // 3 IUnknown + 3 IInspectable + 19 stubs
    private const int VtSlot_GetPersistedDefault = 26;

    // Delegate types cho vtable calls
    // Tất cả dùng stdcall (COM convention trên Windows)
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(
        IntPtr pThis, uint processId, int flow, int role, IntPtr deviceIdHStr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPersistedDefaultAudioEndpointDelegate(
        IntPtr pThis, uint processId, int flow, int role, out IntPtr deviceIdHStr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr pThis);

    // ── Instance state ──────────────────────────────────────────────────────
    private readonly IntPtr _pFactory;
    private readonly SetPersistedDefaultAudioEndpointDelegate _setEndpoint;
    private readonly GetPersistedDefaultAudioEndpointDelegate _getEndpoint;
    private readonly ReleaseDelegate _release;
    private bool _disposed;

    private AudioPolicyConfigService(
        IntPtr pFactory,
        SetPersistedDefaultAudioEndpointDelegate setEndpoint,
        GetPersistedDefaultAudioEndpointDelegate getEndpoint,
        ReleaseDelegate release)
    {
        _pFactory = pFactory;
        _setEndpoint = setEndpoint;
        _getEndpoint = getEndpoint;
        _release = release;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    public static AudioPolicyConfigService Create()
    {
        int build = Environment.OSVersion.Version.Build;
        if (build < MinBuild)
            throw new NotSupportedException(
                $"Per-app device routing requires Windows 11 (build ≥ {MinBuild}). Current build: {build}");

        IntPtr classNameHstr = IntPtr.Zero;
        try
        {
            int hr = Combase.WindowsCreateString(WinRtClass, WinRtClass.Length, out classNameHstr);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);

            Guid iid = IID;
            hr = Combase.RoGetActivationFactory(classNameHstr, ref iid, out IntPtr pFactory);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            if (pFactory == IntPtr.Zero)
                throw new InvalidOperationException("RoGetActivationFactory returned null.");

            // Đọc vtable pointer: *pFactory = pointer tới vtable array
            IntPtr vtable = Marshal.ReadIntPtr(pFactory);

            // Lấy function pointer từ vtable slots
            IntPtr pRelease = Marshal.ReadIntPtr(vtable, VtSlot_Release * IntPtr.Size);
            IntPtr pSet = Marshal.ReadIntPtr(vtable, VtSlot_SetPersistedDefault * IntPtr.Size);
            IntPtr pGet = Marshal.ReadIntPtr(vtable, VtSlot_GetPersistedDefault * IntPtr.Size);

            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(pRelease);
            var setDel = Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointDelegate>(pSet);
            var getDel = Marshal.GetDelegateForFunctionPointer<GetPersistedDefaultAudioEndpointDelegate>(pGet);

            return new AudioPolicyConfigService(pFactory, setDel, getDel, release);
        }
        finally
        {
            if (classNameHstr != IntPtr.Zero)
                Combase.WindowsDeleteString(classNameHstr);
        }
    }

    // ── Output (Render) ─────────────────────────────────────────────────────

    public void SetAppOutput(uint pid, string? deviceId)
        => SetEndpoint(pid, 0 /* eRender */, deviceId);

    public string? GetAppOutput(uint pid)
        => GetEndpoint(pid, 0 /* eRender */);

    // ── Input (Capture) ─────────────────────────────────────────────────────

    public void SetAppInput(uint pid, string? deviceId)
        => SetEndpoint(pid, 1 /* eCapture */, deviceId);

    public string? GetAppInput(uint pid)
        => GetEndpoint(pid, 1 /* eCapture */);

    // ── Device ID format ───────────────────────────────────────────────────
    // SetPersistedDefaultAudioEndpoint cần full SWD path, KHÔNG phải MMDevice.ID ngắn.
    // Format: \\?\SWD#MMDEVAPI#{mmdevice-id}#{interface-guid}
    //   Render:  #{e6327cad-dcec-4949-ae8a-991e976a79d2}
    //   Capture: #{2eef81be-33fa-4800-9670-1cd474972c3f}
    private const string MMDEVAPI_TOKEN = @"\\?\SWD#MMDEVAPI#";
    private const string DEVINTERFACE_AUDIO_RENDER  = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string DEVINTERFACE_AUDIO_CAPTURE = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private static string GenerateDeviceId(string mmdeviceId, int flow)
        => $"{MMDEVAPI_TOKEN}{mmdeviceId}{(flow == 0 ? DEVINTERFACE_AUDIO_RENDER : DEVINTERFACE_AUDIO_CAPTURE)}";

    private static string UnpackDeviceId(string fullId)
    {
        if (fullId.StartsWith(MMDEVAPI_TOKEN, StringComparison.OrdinalIgnoreCase))
            fullId = fullId[MMDEVAPI_TOKEN.Length..];
        if (fullId.EndsWith(DEVINTERFACE_AUDIO_RENDER, StringComparison.OrdinalIgnoreCase))
            fullId = fullId[..^DEVINTERFACE_AUDIO_RENDER.Length];
        else if (fullId.EndsWith(DEVINTERFACE_AUDIO_CAPTURE, StringComparison.OrdinalIgnoreCase))
            fullId = fullId[..^DEVINTERFACE_AUDIO_CAPTURE.Length];
        return fullId;
    }

    // ── Private helpers ─────────────────────────────────────────────────────

    private void SetEndpoint(uint pid, int flow, string? deviceId)
    {
        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (deviceId is not null)
            {
                // Transform MMDevice.ID → full SWD path
                string fullId = GenerateDeviceId(deviceId, flow);
                int hr = Combase.WindowsCreateString(fullId, fullId.Length, out hstring);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
            // Set cả 3 roles (Console=0, Multimedia=1, Communications=2) giống Windows Settings
            for (int role = 0; role <= 2; role++)
            {
                _setEndpoint(_pFactory, pid, flow, role, hstring);
            }
        }
        finally
        {
            if (hstring != IntPtr.Zero)
                Combase.WindowsDeleteString(hstring);
        }
    }

    private string? GetEndpoint(uint pid, int flow)
    {
        try
        {
            int hr = _getEndpoint(_pFactory, pid, flow, 1 /* Multimedia */, out IntPtr hstr);
            if (hr != 0 || hstr == IntPtr.Zero) return null;
            try
            {
                string? result = HStringToString(hstr);
                if (string.IsNullOrEmpty(result)) return null;
                // Unpack full SWD path → MMDevice.ID ngắn
                return UnpackDeviceId(result);
            }
            finally
            {
                Combase.WindowsDeleteString(hstr);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? HStringToString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return null;
        IntPtr buf = Combase.WindowsGetStringRawBuffer(hstring, out int length);
        if (buf == IntPtr.Zero || length == 0) return null;
        return Marshal.PtrToStringUni(buf, length);
    }

    // ── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed && _pFactory != IntPtr.Zero)
        {
            _release(_pFactory);
            _disposed = true;
        }
    }
}

// ── P/Invoke: combase.dll ────────────────────────────────────────────────────
internal static class Combase
{
    // RoGetActivationFactory — nhận IntPtr thô vì .NET 8 không marshal HString/IInspectable
    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int RoGetActivationFactory(
        IntPtr activatableClassId,    // HSTRING
        ref Guid iid,
        out IntPtr factory);          // raw COM pointer

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string? sourceString,
        int length,
        out IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    public static extern IntPtr WindowsGetStringRawBuffer(
        IntPtr hstring,
        out int length);
}
