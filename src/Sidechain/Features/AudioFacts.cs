namespace Sidechain.Features;

/// <summary>
/// Answers of the stage 0 spike (2026-10-21, Windows 11 26300, NAudio.Wasapi 3.1.0), measured with own test tones
/// on a non-default device: session volume 1 / 0.5 / 0.25 / 0.1, device loopback and process loopback.
/// </summary>
public static class AudioFacts
{
    /// <summary>The session peak meter is taken before the session volume: at volume 0.5 it still reads the full peak.
    /// So ducking a target never changes its meter, and the user's slider does not move a trigger's threshold.</summary>
    public const bool SessionPeakIsPreVolume = true;

    /// <summary>SetMasterVolume is linear in amplitude: 0.5 → −6.02 dB, 0.25 → −12.04 dB, 0.1 → −20.00 dB by loopback.</summary>
    public const bool SessionVolumeIsLinear = true;

    /// <summary>Process loopback (v1.0 leveler) captures after the session volume: 0.5 → −6.02 dB.</summary>
    public const bool LoopbackIsPostVolume = true;

    /// <summary>Stage 7 spike: capturing a parent PID with IncludeTargetProcessTree gets a child's sound (a browser's audio
    /// service). One capture costs ~0.3 % of one core with the RMS loop; it needs BuildAsync, not Build.</summary>
    public const bool LoopbackFollowsTree = true;

    /// <summary>IPolicyConfig::SetDefaultEndpoint with the GUIDs from PolicyConfig.cs works on 26300 (read back after the call).</summary>
    public const bool PolicyConfigWorks = true;

    /// <summary>
    /// Writing UserDuckingPreference to the registry does not apply at once: with 0 and with 3 the next call is still ducked.
    /// It is read later (sign-out or restart, not checked). Windows ducking (−18 dB at the default "−80 %") only touches
    /// sessions on the communications device and is not visible in the session volume or peak.
    /// </summary>
    public const bool WindowsDuckingAppliesLive = false;

    // Not checked (owner's UAC slot): whether a session of an elevated process can be controlled without elevation.
}
