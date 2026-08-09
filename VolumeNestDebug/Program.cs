using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

// Test RoGetActivationFactory với HSTRING thủ công
Console.WriteLine("=== RoGetActivationFactory (HSTRING manual) ===");
string className = "Windows.Media.Internal.AudioPolicyConfig";
IntPtr classNameHstr = IntPtr.Zero;
try {
    int hr2 = WindowsCreateString(className, className.Length, out classNameHstr);
    Console.WriteLine($"WindowsCreateString HR: 0x{hr2:X8}");

    var iid = new Guid("ab3d4648-e242-459f-b02f-541c70306324");
    int hr = RoGetActivationFactory(classNameHstr, ref iid, out object factory);
    Console.WriteLine($"RoGetActivationFactory HR: 0x{hr:X8}");
    Console.WriteLine($"Factory null: {factory == null}");
    if (factory != null) Console.WriteLine($"Factory type: {factory.GetType().Name}");
} catch (Exception ex) {
    Console.WriteLine($"Exception: {ex.GetType().Name}: {ex.Message}");
} finally {
    if (classNameHstr != IntPtr.Zero) WindowsDeleteString(classNameHstr);
}

// Test tất cả render devices sessions
Console.WriteLine("\n=== All Render Device Sessions ===");
var enumerator = new MMDeviceEnumerator();
var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
Console.WriteLine($"Render devices: {renderDevices.Count}");
for (int d = 0; d < renderDevices.Count; d++) {
    var dev = renderDevices[d];
    Console.WriteLine($"\n  Device [{d}]: {dev.FriendlyName}");
    try {
        var mgr = dev.AudioSessionManager;
        mgr.RefreshSessions();
        Console.WriteLine($"  Sessions: {mgr.Sessions.Count}");
        for (int i = 0; i < mgr.Sessions.Count; i++) {
            var s = mgr.Sessions[i];
            string procName = "?";
            try { procName = Process.GetProcessById((int)s.GetProcessID).ProcessName; } catch {}
            Console.WriteLine($"    [{i}] PID={s.GetProcessID} '{procName}' State={s.State} System={s.IsSystemSoundsSession}");
        }
    } catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
}

[DllImport("combase.dll", PreserveSig = true)]
static extern int RoGetActivationFactory(
    IntPtr activatableClassId,
    ref Guid iid,
    [MarshalAs(UnmanagedType.IUnknown)] out object factory);

[DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
static extern int WindowsCreateString(
    [MarshalAs(UnmanagedType.LPWStr)] string? s, int len, out IntPtr h);

[DllImport("combase.dll", PreserveSig = true)]
static extern int WindowsDeleteString(IntPtr h);
