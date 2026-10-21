using System.Runtime.InteropServices;

namespace Sidechain.Features.Audio;

/// <summary>
/// Undocumented IPolicyConfig (Windows 7+), the interface the Sound control panel uses to set the default device.
/// GUIDs from open sources; checked on Windows 11 26300 (AudioFacts.PolicyConfigWorks). Built-in COM interop, no trimming.
/// </summary>
static class PolicyConfig
{
    public static void SetDefault(string deviceId, int role) => Marshal.ThrowExceptionForHR(((IPolicyConfig)new CPolicyConfigClient()).SetDefaultEndpoint(deviceId, role));

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class CPolicyConfigClient;

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        // Vtable slots before SetDefaultEndpoint; never called, so their signatures don't matter
        void GetMixFormat(); void GetDeviceFormat(); void ResetDeviceFormat(); void SetDeviceFormat();
        void GetProcessingPeriod(); void SetProcessingPeriod(); void GetShareMode(); void SetShareMode();
        void GetPropertyValue(); void SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role); // ERole: 0 console, 1 multimedia, 2 communications
    }
}
