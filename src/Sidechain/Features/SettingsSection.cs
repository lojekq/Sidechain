using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Sidechain.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Sidechain.Features;

/// <summary>The Sidechain part of Settings (SettingsPage.AppSection): Windows call ducking and "Restore all volumes".</summary>
public static class SettingsSection
{
    public static FrameworkElement Create()
    {
        var state = new TextBlock { FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextWrapping = TextWrapping.Wrap };
        var off = new Button { Content = Loc.T("WinDuck.TurnOff"), Appearance = ControlAppearance.Primary, Margin = new(0, 0, 8, 0) };
        var restore = new Button();
        void Sync()
        {
            state.Text = string.Format(Loc.T("WinDuck.State"), WindowsDucking.Describe(WindowsDucking.Raw));
            off.Visibility = WindowsDucking.Ducks ? Visibility.Visible : Visibility.Collapsed;
            var backup = Settings.Current.WindowsDuckingBackup;
            restore.Visibility = backup is null ? Visibility.Collapsed : Visibility.Visible;
            restore.Content = string.Format(Loc.T("WinDuck.Restore"), WindowsDucking.Describe(backup ?? 1));
        }
        off.Click += (_, _) => { WindowsDucking.TurnOff(); Sync(); };
        restore.Click += (_, _) => { WindowsDucking.RestoreAsItWas(); Sync(); };
        Sync();

        var sound = new HyperlinkButton { Content = Loc.T("WinDuck.Open"), Padding = new(0), Margin = new(0, 4, 0, 0) };
        sound.Click += (_, _) => { if (!Args.Current.Demo) Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,3") { UseShellExecute = true }); };

        var hotkey = new HotkeyBox { Hotkey = Settings.Current.PauseHotkey, Status = PauseHotkey.Apply() };
        hotkey.Changed += (_, _) =>
        {
            Settings.Current.PauseHotkey = hotkey.Hotkey;
            Settings.Save();
            hotkey.Status = PauseHotkey.Apply();
        };

        var restoreAll = new Button { Content = Loc.T("Restore.All") };
        restoreAll.Click += (_, _) => Engine.Current?.RestoreAllVolumes();

        var section = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = AppInfo.DisplayName, FontTypography = FontTypography.BodyStrong, Margin = new(0, 0, 0, 8) },
                new CardControl
                {
                    Icon = new SymbolIcon(SymbolRegular.Call24) { FontSize = 24 }, Margin = new(0, 0, 0, 4),
                    Header = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = Loc.T("WinDuck.Title") },
                            state,
                            new TextBlock { Text = Loc.T("WinDuck.Note"), FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextWrapping = TextWrapping.Wrap },
                            sound,
                        },
                    },
                    Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { off, restore } },
                },
                new InfoBar { Title = Loc.T("WinDuck.DiscordTitle"), Message = Loc.T("WinDuck.Discord"), IsOpen = true, IsClosable = false, Margin = new(0, 0, 0, 4) },
                new CardControl
                {
                    Icon = new SymbolIcon(SymbolRegular.Keyboard24) { FontSize = 24 }, Margin = new(0, 0, 0, 4),
                    Header = Loc.T("Hotkey.Pause"), Content = hotkey,
                },
                new CardControl
                {
                    Icon = new SymbolIcon(SymbolRegular.ArrowUndo24) { FontSize = 24 }, Margin = new(0, 0, 0, 4),
                    Header = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = Loc.T("Restore.All") },
                            new TextBlock { Text = Loc.T("Restore.Note"), FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextWrapping = TextWrapping.Wrap },
                        },
                    },
                    Content = restoreAll,
                },
            },
        };
        section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush"); // a Page's default foreground is black
        if (Args.Current.Demo) section.Loaded += (_, _) => section.BringIntoView(); // the settings screenshot shows this part
        return section;
    }
}

/// <summary>v1.0: one combination (00-shared §9.5, Ctrl+Alt+Shift+S) pauses until resumed, or resumes.</summary>
public static class PauseHotkey
{
    const int Id = 1;

    /// <summary>At start and after a change; --demo registers nothing (Shell.Hotkeys).</summary>
    public static HotkeyStatus Apply()
    {
        if (Hotkey.Parse(Settings.Current.PauseHotkey) is { } h) return Hotkeys.Register(Id, h, Toggle);
        Hotkeys.Unregister(Id);
        return HotkeyStatus.Ok;
    }

    static void Toggle()
    {
        if (Engine.Current is not { } e) return;
        if (e.Snapshot.State == EngineState.Paused) e.Resume();
        else e.Pause(null);
    }
}
