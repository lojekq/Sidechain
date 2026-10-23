using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Sidechain.Features.Core;
using Sidechain.Shell;
using Wpf.Ui.Controls;

namespace Sidechain.Features;

/// <summary>A row of the Now page, updated in place 15 times a second.</summary>
public sealed class AppRowVm(string app) : INotifyPropertyChanged
{
    static readonly Brush TriggerBrush = Frozen(Color.FromArgb(0x55, 0x4C, 0xC2, 0xFF)), TargetBrush = Frozen(Color.FromArgb(0x55, 0xF7, 0xA8, 0x00));
    static readonly Color[] Badges = [Color.FromRgb(0x5B, 0x6C, 0xD8), Color.FromRgb(0x2E, 0x9E, 0x7A), Color.FromRgb(0xC2, 0x5B, 0x8F), Color.FromRgb(0xD0, 0x7A, 0x2E), Color.FromRgb(0x3F, 0x8F, 0xC9)];

    public string App { get; } = app;
    public string Title { get; private set; } = app;
    public string Detail { get; private set; } = "";
    public ImageSource? Icon { get; private set; }
    public string Letter => Title.Length > 0 ? Title[..1].ToUpperInvariant() : "?";
    public Brush Badge => Frozen(Badges[App.Sum(c => c) % Badges.Length]);
    public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string RoleText { get; private set; } = "";
    public Brush RoleBrush { get; private set; } = Brushes.Transparent;
    public double Meter { get; private set; }
    public string BaselineText { get; private set; } = "";
    public string GainText { get; private set; } = "";
    public double GainWidth { get; private set; }
    public double? MedianDb { get; private set; }
    public bool Leveled { get; private set; }
    public ControlAppearance LevelAppearance => Leveled ? ControlAppearance.Primary : ControlAppearance.Secondary;
    public Visibility NormVisibility => Leveled ? Visibility.Visible : Visibility.Collapsed;
    public string LevelTip => Loc.T(Leveled ? "Live.LevelOff" : "Live.LevelOn");
    public string BoostText { get; private set; } = "";     // v1.1: the room left, or that the master is at 100 %
    public ControlAppearance BoostAppearance => BoostSet ? ControlAppearance.Primary : ControlAppearance.Secondary;
    bool BoostSet => Settings.Current.Boost.Any(b => b.App == App) || Settings.Current.Leveler.Any(l => l.App == App && l.MaxBoostDb > 0);
    bool boostSet;
    AppRole? role;

    public void Update(AppRow r)
    {
        if (Title != r.Title) { Title = r.Title; Changed(nameof(Title)); Changed(nameof(Letter)); }
        if (Icon is null && r.ExePath is not null && ExeIcon.Get(r.ExePath) is { } icon) { Icon = icon; Changed(nameof(Icon)); Changed(nameof(GlyphVisibility)); }
        var detail = r.Sessions > 1 ? $"{r.Devices} · {string.Format(Loc.T("Live.Streams"), r.Sessions)}" : r.Devices;
        if (r.LevelerDb is { } lev) detail = string.Format(Loc.T("Live.Leveling"), TrayMenu.FormatDb(lev)) + " · " + detail; // first: the device name gets cut
        if (r.BoostDb >= 0.5) detail = string.Format(Loc.T("Boost.Caption"), TrayMenu.FormatDb(r.BoostDb)) + " · " + detail;
        Set(nameof(BoostText), BoostText, r.BoostLimited ? Loc.T("Boost.Limited")
            : r.BoostRoomDb >= 0.5 ? string.Format(Loc.T("Boost.Room"), TrayMenu.FormatDb(r.BoostRoomDb)) : Loc.T("Boost.NoRoom"), v => BoostText = v);
        if (boostSet != BoostSet) { boostSet = BoostSet; Changed(nameof(BoostAppearance)); }
        Set(nameof(Detail), Detail, detail, v => Detail = v);
        MedianDb = r.LevelMedianDb;
        if (Leveled != r.LevelerDb.HasValue)
        {
            Leveled = r.LevelerDb.HasValue;
            Changed(nameof(LevelAppearance)); Changed(nameof(NormVisibility)); Changed(nameof(LevelTip));
        }
        if (role != r.Role)
        {
            role = r.Role;
            RoleText = Loc.T($"Role.{r.Role}");
            RoleBrush = r.Role switch { AppRole.Trigger => TriggerBrush, AppRole.Target => TargetBrush, _ => Brushes.Transparent };
            Changed(nameof(RoleText)); Changed(nameof(RoleBrush));
        }
        var level = Math.Clamp((Db.FromScalar(r.Peak) + 60) / 60, 0, 1);
        Meter = Math.Max(level, Meter - 0.08); // falls slowly, like a real meter
        Changed(nameof(Meter));
        Set(nameof(BaselineText), BaselineText, $"{Math.Round(r.Baseline * 100)} %", v => BaselineText = v);
        Set(nameof(GainText), GainText, TrayMenu.FormatDb(r.GainDb), v => GainText = v);
        var width = Math.Round(120 * Math.Clamp(Math.Abs(r.GainDb) / 30, 0, 1));
        if (width != GainWidth) { GainWidth = width; Changed(nameof(GainWidth)); }
    }

    void Set(string name, string old, string value, Action<string> set)
    {
        if (old == value) return;
        set(value);
        Changed(name);
    }

    static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new(p));
}

public partial class LivePage
{
    readonly ObservableCollection<AppRowVm> rows = [];
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(66) }; // 15 Hz, only while the page is shown

    public LivePage()
    {
        InitializeComponent();
        AppList.ItemsSource = rows;
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { WinDuckBar.IsOpen = WindowsDucking.Ducks; Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
        WinDuckOff.Click += (_, _) => { WindowsDucking.TurnOff(); WinDuckBar.IsOpen = WindowsDucking.Ducks; };
    }

    /// <summary>v1.0 leveler on or off for the row's app, 3 apps at most (one capture each).</summary>
    void LevelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRowVm row) return;
        var list = Settings.Current.Leveler;
        if (list.RemoveAll(x => x.App == row.App) == 0)
        {
            if (list.Count >= Engine.MaxLeveled) { Tray.Notify(AppInfo.DisplayName, string.Format(Loc.T("Live.LevelMax"), Engine.MaxLeveled)); return; }
            list.Add(new LevelerEntry(row.App, Leveler.DefaultTargetDb));
        }
        Settings.Save();
        Engine.Current?.SetLeveler(list);
    }

    /// <summary>
    /// v1.1 boost of the row's app: by hand (0–20 dB) and automatically (the leveler lifts quiet parts up to 12 dB).
    /// The room and the honest limit are shown live: past 100 % only the device master helps.
    /// </summary>
    void BoostClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AppRowVm row } button) return;
        var s = Settings.Current;
        var value = new System.Windows.Controls.TextBlock { FontWeight = FontWeights.SemiBold };
        var slider = new Slider { Minimum = 0, Maximum = Engine.MaxBoostDb, IsSnapToTickEnabled = true, TickFrequency = 1, Width = 300,
                                  Value = s.Boost.FirstOrDefault(b => b.App == row.App)?.Db ?? 0 };
        void Show() => value.Text = slider.Value > 0 ? TrayMenu.FormatDb(slider.Value) : Loc.T("Boost.Off");
        Show();
        slider.ValueChanged += (_, _) =>
        {
            Show();
            s.Boost.RemoveAll(b => b.App == row.App);
            if (slider.Value > 0) s.Boost.Add(new BoostEntry(row.App, slider.Value));
            Settings.Save();
            Engine.Current?.SetBoosts(s.Boost);
        };
        var entry = s.Leveler.FirstOrDefault(l => l.App == row.App);
        var auto = new CheckBox
        {
            Content = string.Format(Loc.T("Boost.Auto"), TrayMenu.FormatDb(Leveler.DefaultMaxBoostDb)), Margin = new(0, 12, 0, 0),
            IsChecked = entry?.MaxBoostDb > 0, IsEnabled = entry is not null || s.Leveler.Count < Engine.MaxLeveled,
        };
        auto.Click += (_, _) =>
        {
            var i = s.Leveler.FindIndex(l => l.App == row.App);
            var max = auto.IsChecked == true ? Leveler.DefaultMaxBoostDb : 0;
            if (i >= 0) s.Leveler[i] = s.Leveler[i] with { MaxBoostDb = max };
            else if (max > 0) s.Leveler.Add(new LevelerEntry(row.App, Leveler.DefaultTargetDb, max));
            Settings.Save();
            Engine.Current?.SetLeveler(s.Leveler);
        };
        var room = new System.Windows.Controls.TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0) };
        room.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(AppRowVm.BoostText)) { Source = row });
        var note = new System.Windows.Controls.TextBlock { Text = Loc.T("Boost.How"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), FontSize = 12, Opacity = 0.75 };
        var panel = new StackPanel
        {
            Width = 340,
            Children =
            {
                new System.Windows.Controls.TextBlock { Text = string.Format(Loc.T("Boost.Title"), row.Title), FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) },
                new DockPanel { Children = { value, slider } },
                auto, room, note,
            },
        };
        DockPanel.SetDock(value, Dock.Right);
        var border = new Border { Child = panel, Padding = new(16), CornerRadius = new(8), BorderThickness = new(1) };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        border.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
        new Popup { Child = border, PlacementTarget = button, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, IsOpen = true };
    }

    /// <summary>"Make the current level the norm": the median of the last 30 s becomes the target.</summary>
    void NormClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AppRowVm { MedianDb: { } median } row) return;
        var list = Settings.Current.Leveler;
        var i = list.FindIndex(x => x.App == row.App);
        if (i < 0) return;
        list[i] = list[i] with { TargetDb = Math.Round(median, 1) };
        Settings.Save();
        Engine.Current?.SetLeveler(list);
    }

    void Refresh()
    {
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        var (text, color) = s.State switch
        {
            EngineState.Error => (Loc.T("State.Error"), Color.FromRgb(0xE8, 0x11, 0x23)),
            EngineState.Paused => (s.PausedUntil is { } u ? string.Format(Loc.T("State.PausedUntil"), u) : Loc.T("State.Paused"), Color.FromRgb(0x8A, 0x8A, 0x8A)),
            EngineState.Ducking => (s.Speaking.Count > 0 ? string.Format(Loc.T("State.DuckingBy"), string.Join(", ", s.Speaking)) : Loc.T("State.Ducking"), Color.FromRgb(0x4C, 0xC2, 0xFF)),
            _ => (Loc.T("State.Working"), Color.FromRgb(0x3C, 0xB3, 0x71)),
        };
        if (StatusText.Text != text) StatusText.Text = text;
        if (StatusDot.Fill is not SolidColorBrush b || b.Color != color) StatusDot.Fill = new SolidColorBrush(color);

        var ordered = s.Apps.OrderBy(a => a.Role switch { AppRole.Trigger => 0, AppRole.Target => 1, _ => 2 }).ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var at = -1;
            for (var j = i; j < rows.Count; j++) if (rows[j].App == ordered[i].App) { at = j; break; }
            if (at < 0) rows.Insert(i, new AppRowVm(ordered[i].App));
            else if (at != i) rows.Move(at, i);
            rows[i].Update(ordered[i]);
        }
        while (rows.Count > ordered.Count) rows.RemoveAt(rows.Count - 1);
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
