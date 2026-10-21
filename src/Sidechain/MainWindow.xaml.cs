using Sidechain.Features;
using Sidechain.Shell;

namespace Sidechain;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        ElevatedBar.IsOpen = AppInfo.IsElevated;
        Loaded += (_, _) => { if (Nav.SelectedItem is null) Navigate("live"); };
        IsVisibleChanged += (_, _) => { if (Engine.Current is { } e) e.WindowVisible = IsVisible; }; // all meters only while shown
    }

    /// <summary>By TargetPageTag: "live", "rules", "devices", "settings", "about".</summary>
    public void Navigate(string page) => Nav.Navigate(page);

    public void ShowUpdate(string version, string url)
    {
        UpdateBar.Message = string.Format(Loc.T("Update.Message"), version);
        UpdateLink.NavigateUri = url;
        UpdateBar.IsOpen = true;
    }
}
