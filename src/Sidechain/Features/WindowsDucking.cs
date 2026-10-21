using Microsoft.Win32;
using Sidechain.Shell;

namespace Sidechain.Features;

/// <summary>
/// Windows' own call ducking: HKCU\Software\Microsoft\Multimedia\Audio, DWORD UserDuckingPreference —
/// 0 mute other sounds, 1 −80 % (default, also when the value is missing), 2 −50 %, 3 do nothing.
/// Changed only by the user's click; the old value is kept for "Restore as it was". Windows reads it later,
/// not at once (AudioFacts.WindowsDuckingAppliesLive). --demo shows "do nothing" and writes nothing.
/// </summary>
public static class WindowsDucking
{
    const string Key = @"Software\Microsoft\Multimedia\Audio", Value = "UserDuckingPreference";
    public const int Absent = -1, DoNothing = 3;

    public static int Raw
    {
        get
        {
            if (Args.Current.Demo) return DoNothing;
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Value) is int v ? v : Absent;
        }
    }

    public static int Effective => Raw is >= 0 and <= 3 ? Raw : 1;
    public static bool Ducks => Effective != DoNothing;

    public static void TurnOff()
    {
        if (Args.Current.Demo) return;
        Settings.Current.WindowsDuckingBackup ??= Raw;
        Settings.Save();
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(Value, DoNothing, RegistryValueKind.DWord);
        Log.Info($"Windows call ducking off (was {Settings.Current.WindowsDuckingBackup})");
    }

    public static void RestoreAsItWas()
    {
        if (Args.Current.Demo || Settings.Current.WindowsDuckingBackup is not { } old) return;
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
            if (old == Absent) k.DeleteValue(Value, false);
            else k.SetValue(Value, old, RegistryValueKind.DWord);
        Settings.Current.WindowsDuckingBackup = null;
        Settings.Save();
        Log.Info($"Windows call ducking restored to {old}");
    }

    /// <summary>"−80 %" etc. for a raw value; missing = Windows default.</summary>
    public static string Describe(int raw) => Loc.T($"WinDuck.{(raw is >= 0 and <= 3 ? raw : 1)}");
}
