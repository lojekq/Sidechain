using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using Sidechain.Demo;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;
using Sidechain.Shell;

namespace Sidechain.Features;

/// <summary>Starts the audio engine after the shell has loaded settings, and wires it to exit, sleep, balloons and settings.</summary>
public static class Startup
{
    public static void Init()
    {
        var demo = Args.Current.Demo;
        var s = Settings.Current;
        s.Rules ??= Rules.Default();
        var journal = RestoreJournal.Load(demo ? null : Path.Combine(AppInfo.DataDir, "restore.json"), DateTime.Now);
        var engine = Engine.Current = new Engine(() => demo ? new DemoBackend() : new CoreAudioBackend(), journal);
        var ui = Dispatcher.CurrentDispatcher;

        engine.AutoSwitched += e => ui.BeginInvoke(() => Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Pin.Switched"), e.To.Name), "devices"));
        engine.PinWar += mic => ui.BeginInvoke(() =>
        {
            if (mic) s.Pin.Mic.Enabled = false;
            else s.Pin.Enabled = false;
            Settings.Save();
            Tray.Notify(AppInfo.DisplayName, Loc.T("Pin.War"), "devices");
        });
        engine.PinFailed += () => ui.BeginInvoke(() => Tray.Notify(AppInfo.DisplayName, Loc.T("Pin.Broken"), "devices"));
        engine.FirstDefault += (d, mic) => ui.BeginInvoke(() =>
        {
            var list = mic ? s.Pin.Mic.Priority : s.Pin.Priority;
            if (list.Count == 0) list.Add(d);
            if (mic) s.Pin.Mic.Seeded = true;
            else s.Pin.Seeded = true;
            Settings.Save();
            engine.SetPin(s.Pin);
        });

        App.Exiting += engine.Stop; // tray Exit, Windows session end, crash: volumes back first
        if (!demo)
            SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == PowerModes.Suspend) engine.Suspend();
                else if (e.Mode == PowerModes.Resume) engine.Resumed();
            };
        if (demo) DemoData.Fill(engine);
        engine.Start(s.Rules, s.Pin, s.Leveler, s.Boost);
        PauseHotkey.Apply();
        SettingsPage.AppSection = SettingsSection.Create;
    }
}
