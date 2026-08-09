using System.Runtime.InteropServices;

namespace VolumeNest;

/// <summary>
/// COM interop cho IPolicyConfig — chuyển đầu ra mặc định hệ thống.
/// Dùng raw vtable vì nhất quán với AudioPolicyConfigService.
///
/// CLSID: {870AF99C-171D-4F9E-AF0D-E63DF40C2BC9}  (CPolicyConfigClient)
/// IID:   {F8679F50-850A-41CF-9C72-430F290290C8}  (IPolicyConfig, Win7→Present)
///
/// vtable layout (InterfaceIsIUnknown):
///   slots 0-2:   IUnknown (QI, AddRef, Release)
///   slots 3-10:  8 unused internal methods
///   slot  11:    GetPropertyValue
///   slot  12:    SetPropertyValue
///   slot  13:    SetDefaultEndpoint(PCWSTR deviceId, int role) ← DÙNG
///   slot  14:    SetEndpointVisibility
/// </summary>
public sealed class PolicyConfigService : IDisposable
{
    private static readonly Guid CLSID = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid IID   = new("F8679F50-850A-41CF-9C72-430F290290C8");

    private const int VtSlot_Release = 2;
    private const int VtSlot_SetDefaultEndpoint = 13;  // 3 IUnknown + 8 unused + GetProp + SetProp

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr pThis);

    // SetDefaultEndpoint(PCWSTR wszDeviceId, ERole eRole)
    // deviceId là MMDevice.ID trực tiếp (không cần SWD path)
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int SetDefaultEndpointDelegate(IntPtr pThis,
        [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

    private readonly IntPtr _pConfig;
    private readonly ReleaseDelegate _release;
    private readonly SetDefaultEndpointDelegate _setDefault;
    private bool _disposed;

    private PolicyConfigService(IntPtr pConfig, ReleaseDelegate release, SetDefaultEndpointDelegate setDefault)
    {
        _pConfig = pConfig;
        _release = release;
        _setDefault = setDefault;
    }

    // ── Factory ─────────────────────────────────────────────────────────────

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
        ref Guid riid, out IntPtr ppv);

    private const uint CLSCTX_INPROC_SERVER = 1;

    public static PolicyConfigService Create()
    {
        Guid clsid = CLSID;
        Guid iid = IID;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr pConfig);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        if (pConfig == IntPtr.Zero)
            throw new InvalidOperationException("CoCreateInstance returned null for PolicyConfigClient.");

        IntPtr vtable = Marshal.ReadIntPtr(pConfig);
        IntPtr pRelease = Marshal.ReadIntPtr(vtable, VtSlot_Release * IntPtr.Size);
        IntPtr pSetDefault = Marshal.ReadIntPtr(vtable, VtSlot_SetDefaultEndpoint * IntPtr.Size);

        var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(pRelease);
        var setDefault = Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointDelegate>(pSetDefault);

        return new PolicyConfigService(pConfig, release, setDefault);
    }

    // ── API ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Chuyển đầu ra mặc định hệ thống sang device chỉ định.
    /// deviceId: MMDevice.ID (ví dụ: "{0.0.0.00000000}.{guid}")
    /// </summary>
    public void SetDefaultRenderDevice(string deviceId)
    {
        // Set cả 3 roles giống Windows Settings
        for (int role = 0; role <= 2; role++)
        {
            int hr = _setDefault(_pConfig, deviceId, role);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }
    }

    // ── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed && _pConfig != IntPtr.Zero)
        {
            _release(_pConfig);
            _disposed = true;
        }
    }
}
