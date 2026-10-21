using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Win32;
using Sidechain.Shell;

namespace Sidechain.Features;

/// <summary>
/// Tray items (pause, output device, pinning, restore), and the icon and tooltip that follow the engine twice a second.
/// The icon is drawn here: idle / ducking (accent) / paused (grey), plus a red dot when audio fails or pinning gave up.
/// </summary>
public static class TrayMenu
{
    static MenuItem? pinItem, resumeItem;

    public static IEnumerable<Control> Items()
    {
        var pause = new MenuItem { Header = Loc.T("Tray.Pause") };
        pause.Items.Add(Tray.Item(Loc.T("Tray.Pause15"), () => Pause(TimeSpan.FromMinutes(15))));
        pause.Items.Add(Tray.Item(Loc.T("Tray.Pause60"), () => Pause(TimeSpan.FromHours(1))));
        pause.Items.Add(Tray.Item(Loc.T("Tray.PauseOn"), () => Pause(null)));
        pause.Items.Add(new Separator());
        pause.Items.Add(resumeItem = Tray.Item(Loc.T("Tray.Resume"), () => Engine.Current?.Resume()));
        pause.SubmenuOpened += (_, _) => resumeItem.IsEnabled = Engine.Current?.Snapshot.State == EngineState.Paused;
        yield return pause;

        var device = new MenuItem { Header = Loc.T("Tray.Device") };
        device.Items.Add(new MenuItem()); // a placeholder, so the arrow shows
        device.SubmenuOpened += (_, _) =>
        {
            device.Items.Clear();
            var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
            foreach (var d in s.Devices)
            {
                var item = Tray.Item(d.Name, () => Engine.Current?.SetDefault(d.Id)); // the user's choice, never contested
                item.IsChecked = d.Id == s.DefaultId;
                device.Items.Add(item);
            }
        };
        yield return device;

        pinItem = Tray.Item(Loc.T("Tray.Pin"), () => SetPin(!Settings.Current.Pin.Enabled));
        yield return pinItem;
        yield return Tray.Item(Loc.T("Restore.All"), () => Engine.Current?.RestoreAllVolumes());
    }

    static void Pause(TimeSpan? span) => Engine.Current?.Pause(span is { } t ? DateTime.Now + t : null);

    public static void SetPin(bool on)
    {
        Settings.Current.Pin.Enabled = on;
        Settings.Save();
        Engine.Current?.SetPin(Settings.Current.Pin);
    }

    /// <summary>After Tray.Create: the 2 Hz timer for the icon, the tooltip and the pin check mark.</summary>
    public static void Start()
    {
        // ponytail: Shell's Tray keeps its TaskbarIcon private and the template is frozen; reflection until the template exposes it
        var icon = typeof(Tray).GetField("icon", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as TaskbarIcon;
        if (icon is null) return;
        var images = new Dictionary<(EngineState, bool, bool), byte[]>(); // bytes: TaskbarIcon disposes an icon it replaces
        System.Drawing.Icon? shown = null;
        (EngineState, bool, bool)? shownKey = null;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
            var red = s.State == EngineState.Error || s.PinBroken;
            var key = (s.State, red, LightTaskbar());
            if (shownKey != key || !ReferenceEquals(icon.Icon, shown)) // Tray sets its own icon on a theme change
            {
                if (!images.TryGetValue(key, out var bytes)) images[key] = bytes = Draw(s.State, red, key.Item3);
                icon.Icon = shown = new System.Drawing.Icon(new MemoryStream(bytes));
                shownKey = key;
            }
            var tip = Tooltip(s);
            if (icon.ToolTipText != tip) icon.ToolTipText = tip;
            if (pinItem is not null) pinItem.IsChecked = Settings.Current.Pin.Enabled;
        };
        timer.Start();
    }

    static bool LightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    /// <summary>"Sidechain — Discord: Spotify −12 dB, Chrome −12 dB", cut to 127 characters (the tray limit).</summary>
    public static string Tooltip(Snapshot s)
    {
        var text = s.State switch
        {
            EngineState.Error => Loc.T("State.Error"),
            EngineState.Paused => s.PausedUntil is { } until ? string.Format(Loc.T("State.PausedUntil"), until) : Loc.T("State.Paused"),
            _ when s.Apps.Any(a => a.GainDb <= -0.5) =>
                (s.Speaking.Count > 0 ? string.Join(", ", s.Speaking) + ": " : "") +
                string.Join(", ", s.Apps.Where(a => a.GainDb <= -0.5).Select(a => $"{a.Title} {FormatDb(a.GainDb)}")),
            _ => Loc.T("State.Quiet"),
        };
        var tip = $"{AppInfo.DisplayName} — {text}";
        return tip.Length <= 127 ? tip : tip[..126] + "…";
    }

    public static string FormatDb(double db) => string.Format(Loc.T("Db.Format"), Math.Round(db).ToString("0").Replace("-", "−"));

    /// <summary>Three bars: the left one talks, the other two step aside when ducking.</summary>
    static byte[] Draw(EngineState state, bool red, bool lightTaskbar)
    {
        var fg = lightTaskbar ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White;
        var accent = lightTaskbar ? Color.FromRgb(0x00, 0x67, 0xC0) : Color.FromRgb(0x4C, 0xC2, 0xFF);
        var grey = Color.FromRgb(0x8A, 0x8A, 0x8A);
        var (heights, color) = state switch
        {
            EngineState.Ducking => (new[] { 26.0, 10, 10 }, accent),
            EngineState.Paused => (new[] { 16.0, 16, 16 }, grey),
            _ => (new[] { 22.0, 16, 19 }, fg),
        };
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            var brush = new SolidColorBrush(color);
            for (var i = 0; i < 3; i++)
            {
                var h = heights[i];
                dc.DrawRoundedRectangle(brush, null, new Rect(3 + i * 9.5, 29 - h, 7, h), 2.5, 2.5);
            }
            if (red) dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), new Pen(new SolidColorBrush(lightTaskbar ? Colors.White : Colors.Black), 1.5), new Point(25, 25), 6, 6);
        }
        var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        // H.NotifyIcon takes no RenderTargetBitmap as IconSource: an .ico with one PNG image inside
        var png = new MemoryStream();
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        enc.Save(png);
        var ico = new MemoryStream();
        var w = new BinaryWriter(ico);
        w.Write((short)0); w.Write((short)1); w.Write((short)1);                // ICONDIR: icon, one image
        w.Write((byte)32); w.Write((byte)32); w.Write((short)0); w.Write((short)1); w.Write((short)32);
        w.Write((int)png.Length); w.Write(22);                                   // ICONDIRENTRY: size, offset
        w.Write(png.ToArray());
        return ico.ToArray();
    }
}
