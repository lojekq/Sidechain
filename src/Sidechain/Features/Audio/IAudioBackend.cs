using Sidechain.Features.Core;

namespace Sidechain.Features.Audio;

/// <summary>One audio session of an app on an output device. Key = session instance id (unique, changes when the stream is recreated).</summary>
public sealed record AudioSession(string Key, string App, int Pid, string DeviceId);

/// <summary>v1.0 leveler: the loudness of one process tree, measured after the session volume.</summary>
public interface ILevel : IDisposable
{
    int Pid { get; }
    /// <summary>RMS of the last 400 ms in dBFS; NaN while nothing plays.</summary>
    double LevelDb { get; }
}

/// <summary>
/// Core Audio as Sidechain needs it; the real one and the --demo one. Called from the audio thread only.
/// Any method may throw when the audio service restarts: the engine then disposes the backend and makes a new one.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>Sessions of apps on all active output devices; system sounds and expired sessions left out.</summary>
    IReadOnlyList<AudioSession> Enumerate();
    float Peak(AudioSession s);
    float GetVolume(AudioSession s);
    void SetVolume(AudioSession s, float volume);

    /// <summary>Active output devices, or microphones.</summary>
    IReadOnlyList<AudioDevice> Devices(bool capture = false);
    /// <summary>Default device for multimedia (what a player or a recorder gets).</summary>
    string? DefaultId(bool capture = false);
    /// <summary>Console + Multimedia, and Communications if asked. The caller reads the default back to check.</summary>
    void SetDefault(string id, bool communications);

    /// <summary>For the Now page: a readable name ("Google Chrome") and the exe path for the icon; null if unknown.</summary>
    (string Title, string? ExePath) Describe(AudioSession s);

    /// <summary>v1.0 leveler: starts measuring a process tree; null when it can't be measured.</summary>
    ILevel? StartLevel(int pid);
}
