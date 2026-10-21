using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sidechain.Features.Core;
using Sidechain.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Sidechain.Features;

/// <summary>Priority list of output devices, the pin switch, the last automatic switches and "Keep as it was".</summary>
public partial class DevicesPage
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    string shown = "";
    PinEvent? offered;

    static PinSettings Pin => Settings.Current.Pin;

    public DevicesPage()
    {
        InitializeComponent();
        PinSwitch.IsChecked = Pin.Enabled;
        CommBox.IsChecked = Pin.AlsoCommunications;
        PinSwitch.Click += (_, _) => TrayMenu.SetPin(PinSwitch.IsChecked == true);
        MicSwitch.IsChecked = Pin.Mic.Enabled;
        MicSwitch.Click += (_, _) => { Pin.Mic.Enabled = MicSwitch.IsChecked == true; Changed(); };
        CommBox.Click += (_, _) => { Pin.AlsoCommunications = CommBox.IsChecked == true; Changed(); };
        UndoButton.Click += (_, _) => { Engine.Current?.UndoLastAuto(); UndoBar.IsOpen = false; };
        MainButton.Click += (_, _) =>
        {
            var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
            if (s.Devices.FirstOrDefault(d => d.Id == s.DefaultId) is not { } d) return;
            Pin.Priority.RemoveAll(p => p.Id == d.Id);
            Pin.Priority.Insert(0, d);
            Changed();
        };
        timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); timer.Start(); };
        Unloaded += (_, _) => timer.Stop();
    }

    void Changed()
    {
        Settings.Save();
        Engine.Current?.SetPin(Pin);
        shown = "";
        Refresh();
    }

    void Refresh()
    {
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        PinSwitch.IsChecked = Pin.Enabled;
        MicSwitch.IsChecked = Pin.Mic.Enabled;
        if (s.LastAuto != offered) // once per automatic switch; closed by hand stays closed
        {
            offered = s.LastAuto;
            UndoBar.IsOpen = offered is not null;
            if (offered is not null) UndoBar.Message = string.Format(Loc.T("Devices.Undo.Message"), offered.To.Name, offered.From.Name);
        }

        var current = s.Devices.FirstOrDefault(d => d.Id == s.DefaultId);
        MainBar.IsOpen = current is not null && PinPolicy.Best(Pin.Priority, [current]) is null;
        if (current is not null) MainBar.Message = string.Format(Loc.T("Devices.NotListed"), current.Name);

        ProblemBar.IsOpen = s.PinBroken;
        ProblemBar.Title = Loc.T("Pin.Broken");

        var key = string.Join("|", s.Devices.Concat(s.Mics).Select(d => d.Id + d.Name)) + "#" + s.DefaultId + s.MicDefaultId + "#" +
                  string.Join("|", Pin.Priority.Concat(Pin.Mic.Priority).Select(p => p.Id)) + "#" + s.PinLog.Count;
        if (key == shown) return;
        shown = key;
        Render(PriorityList, OtherList, OthersTitle, Pin.Priority, s.Devices, s.DefaultId);
        Render(MicPriorityList, MicOtherList, MicOthersTitle, Pin.Mic.Priority, s.Mics, s.MicDefaultId);

        LogList.Children.Clear();
        foreach (var e in s.PinLog)
            LogList.Children.Add(new TextBlock
            {
                Text = string.Format(Loc.T("Devices.LogLine"), e.At, e.From.Name, e.To.Name), TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 4),
            });
        LogEmpty.Visibility = s.PinLog.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One direction: its priority list with ↑ ↓ ✕, then the active devices not on it with "Add".</summary>
    void Render(StackPanel priorityPanel, StackPanel otherPanel, FrameworkElement othersTitle, List<AudioDevice> priority, IReadOnlyList<AudioDevice> devices, string? defaultId)
    {
        priorityPanel.Children.Clear();
        for (var i = 0; i < priority.Count; i++)
        {
            var index = i;
            var p = priority[i];
            var active = PinPolicy.Best([p], devices);
            var state = active is null ? Loc.T("Devices.Missing") : active.Id == defaultId ? Loc.T("Devices.Default") : Loc.T("Devices.Active");
            priorityPanel.Children.Add(Row($"{i + 1}.  {p.Name}", state, active is not null,
                Icon(SymbolRegular.ArrowUp24, "Devices.Up", index > 0, () => Swap(priority, index, index - 1)),
                Icon(SymbolRegular.ArrowDown24, "Devices.Down", index < priority.Count - 1, () => Swap(priority, index, index + 1)),
                Icon(SymbolRegular.Delete24, "Devices.Remove", true, () => { priority.RemoveAt(index); Changed(); })));
        }

        otherPanel.Children.Clear();
        foreach (var d in devices.Where(d => PinPolicy.Best(priority, [d]) is null))
        {
            var add = new Button { Content = Loc.T("Devices.Add"), Icon = new SymbolIcon(SymbolRegular.Add24) };
            add.Click += (_, _) => { priority.Add(d); Changed(); };
            otherPanel.Children.Add(Row(d.Name, d.Id == defaultId ? Loc.T("Devices.Default") : Loc.T("Devices.Active"), true, add));
        }
        othersTitle.Visibility = otherPanel.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Swap(List<AudioDevice> list, int a, int b)
    {
        (list[a], list[b]) = (list[b], list[a]);
        Changed();
    }

    static Button Icon(SymbolRegular symbol, string tip, bool enabled, Action click)
    {
        var b = new Button { Icon = new SymbolIcon(symbol), ToolTip = Loc.T(tip), IsEnabled = enabled, Margin = new(4, 0, 0, 0) };
        b.Click += (_, _) => click();
        return b;
    }

    static Border Row(string name, string state, bool active, params Button[] buttons)
    {
        var grid = new Grid { ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        grid.Children.Add(new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, Appearance = active ? TextColor.Primary : TextColor.Tertiary },
                new TextBlock { Text = state, FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary },
            },
        });
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) panel.Children.Add(b);
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        var border = new Border { Child = grid, Padding = new(14, 8, 10, 8), Margin = new(0, 0, 0, 4), CornerRadius = new(6), BorderThickness = new(1) };
        border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }
}
