using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Sidechain.Features.Audio;
using Sidechain.Features.Core;
using Sidechain.Shell;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace Sidechain.Features;

/// <summary>The rule list with switches, and the editor: apps from the running ones or typed, threshold over the live meter.</summary>
public partial class RulesPage
{
    const double MeterFloorDb = -70;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    Rule? editing;
    int editingIndex = -1; // -1 = a new rule
    double meter;

    static List<Rule> RuleSet => Settings.Current.Rules ??= Rules.Default();

    public RulesPage()
    {
        InitializeComponent();
        BuildList();
        AddButton.Click += (_, _) => Templates().IsOpen = true;
        TriggerAdd.Click += (_, _) => AddApp(TriggerPick, editing!.Triggers);
        TargetAdd.Click += (_, _) => AddApp(TargetPick, editing!.Targets);
        AllOthersAdd.Click += (_, _) => { if (!editing!.Targets.Contains(Rules.AllOthers)) editing.Targets.Add(Rules.AllOthers); ShowApps(); };
        ThresholdSlider.ValueChanged += (_, _) => { if (editing is null) return; editing.ThresholdDb = ThresholdSlider.Value; ShowValues(); };
        DepthSlider.ValueChanged += (_, _) => { if (editing is null) return; editing.DepthDb = DepthSlider.Value; ShowValues(); };
        BoostSlider.ValueChanged += (_, _) => { if (editing is null) return; editing.BoostTriggersDb = BoostSlider.Value; ShowValues(); };
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();
        MeterBox.SizeChanged += (_, _) => ShowValues();
        timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            if (Args.Current.Demo && editing is null) // the rule-editor screenshot
            {
                Edit(0);
                TimingBox.IsExpanded = true;
            }
            timer.Start();
        };
        Unloaded += (_, _) => timer.Stop();
    }

    static string AppTitle(string app, Snapshot s) =>
        app == Rules.AllOthers ? Loc.T("Rules.AllOthers") : s.Apps.FirstOrDefault(a => a.App == app)?.Title ?? CoreAudioBackend.Title(app);

    /// <summary>"Discord, Telegram → Spotify, Google Chrome +8 · −12 dB".</summary>
    static string Summary(Rule r, Snapshot s)
    {
        static string List(IEnumerable<string> apps, Snapshot s, int max)
        {
            var all = apps.Select(a => AppTitle(a, s)).ToList();
            return all.Count <= max ? string.Join(", ", all) : string.Join(", ", all.Take(max)) + $" +{all.Count - max}";
        }
        var boost = r.BoostTriggersDb > 0 ? " · " + string.Format(Loc.T("Rules.Boost.Summary"), TrayMenu.FormatDb(r.BoostTriggersDb)) : "";
        return $"{List(r.Triggers, s, 3)} → {List(r.Targets, s, 3)} · {TrayMenu.FormatDb(-r.DepthDb)}{boost}";
    }

    void BuildList()
    {
        RuleList.Children.Clear();
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        for (var i = 0; i < RuleSet.Count; i++)
        {
            var index = i;
            var r = RuleSet[i];
            var toggle = new ToggleSwitch { IsChecked = r.Enabled, Margin = new(0, 0, 12, 0) };
            toggle.Click += (_, _) =>
            {
                r.Enabled = toggle.IsChecked == true;
                var errors = Rules.Validate(RuleSet);
                if (r.Enabled && errors.Count > 0)
                {
                    r.Enabled = false;
                    toggle.IsChecked = false;
                    ShowListError(Message(errors[0]));
                    return;
                }
                ListError.IsOpen = false;
                Apply();
            };
            var edit = new Button { Icon = new SymbolIcon(SymbolRegular.Edit24), ToolTip = Loc.T("Rules.Edit"), Margin = new(0, 0, 4, 0) };
            edit.Click += (_, _) => Edit(index);
            var delete = new Button { Icon = new SymbolIcon(SymbolRegular.Delete24), ToolTip = Loc.T("Rules.Delete") };
            delete.Click += (_, _) => { RuleSet.RemoveAt(index); Close(); Apply(); };
            RuleList.Children.Add(new CardControl
            {
                Margin = new(0, 0, 0, 4),
                Icon = new SymbolIcon(SymbolRegular.TextBulletListSquare24) { FontSize = 24 },
                Header = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = string.Format(Loc.T("Rules.Name"), i + 1), FontTypography = FontTypography.BodyStrong },
                        new TextBlock { Text = Summary(r, s), FontTypography = FontTypography.Caption, Appearance = TextColor.Secondary, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { toggle, edit, delete } },
            });
        }
    }

    void ShowListError(string text)
    {
        ListError.Message = text;
        ListError.IsOpen = true;
    }

    static void Apply()
    {
        Settings.Save();
        Engine.Current?.SetRules(RuleSet);
    }

    /// <summary>v1.0: "Add rule" starts empty or from a template; nothing is saved until Save.</summary>
    ContextMenu Templates()
    {
        var menu = new ContextMenu { PlacementTarget = AddButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Item(string key, Func<Rule> make)
        {
            var item = new System.Windows.Controls.MenuItem { Header = Loc.T(key) };
            item.Click += (_, _) => Edit(-1, make());
            menu.Items.Add(item);
        }
        Item("Rules.Template.Empty", () => new Rule());
        Item("Rules.Template.Music", () => new Rule { Triggers = ["discord"], Targets = Rules.Default()[0].Targets });
        Item("Rules.Template.Calls", () => new Rule { Triggers = ["discord", "telegram", "ms-teams", "zoom"], Targets = [Rules.AllOthers] });
        Item("Rules.Template.Game", () => new Rule { Triggers = ["discord"], Targets = ["dota2", "cs2"], DepthDb = 6 });
        return menu;
    }

    void Edit(int index, Rule? template = null)
    {
        if (index >= RuleSet.Count) return;
        editingIndex = index;
        editing = index < 0 ? template ?? new Rule() : RuleSet[index].Clone();
        EditorTitle.Text = index < 0 ? Loc.T("Rules.New") : string.Format(Loc.T("Rules.Name"), index + 1);
        ThresholdSlider.Value = editing.ThresholdDb;
        DepthSlider.Value = editing.DepthDb;
        BoostSlider.Value = editing.BoostTriggersDb;
        AttackBox.Value = editing.AttackMs;
        HoldBox.Value = editing.HoldMs;
        ReleaseBox.Value = editing.ReleaseMs;
        MinSignalBox.Value = editing.MinSignalMs;
        ShowError(TriggerError, "");
        ShowError(TargetError, "");
        ShowApps();
        ShowValues();
        Editor.Visibility = Visibility.Visible;
        Editor.BringIntoView(new Rect(0, 0, 1, 200)); // its top, not its middle
    }

    void Close()
    {
        editing = null;
        Editor.Visibility = Visibility.Collapsed;
        BuildList();
    }

    void ShowApps()
    {
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        Chips(TriggerChips, editing!.Triggers, s);
        Chips(TargetChips, editing.Targets, s);
        Fill(TriggerPick, s, editing.Triggers);
        Fill(TargetPick, s, editing.Targets);
    }

    /// <summary>Chips go before the picker, which lives at the end of the same WrapPanel.</summary>
    void Chips(WrapPanel panel, List<string> apps, Snapshot s)
    {
        foreach (var old in panel.Children.OfType<Button>().Where(b => Equals(b.Tag, "chip")).ToList()) panel.Children.Remove(old);
        var at = 0;
        foreach (var app in apps)
        {
            var remove = new Button { Content = AppTitle(app, s) + "  ✕", Margin = new(0, 0, 6, 6), Padding = new(10, 4, 10, 5), Tag = "chip", ToolTip = Loc.T("Rules.Remove") };
            remove.Click += (_, _) => { apps.Remove(app); ShowApps(); };
            panel.Children.Insert(at++, remove);
        }
    }

    /// <summary>Running apps not on the list yet; any name can be typed too.</summary>
    static void Fill(ComboBox box, Snapshot s, List<string> taken)
    {
        box.Items.Clear();
        foreach (var a in s.Apps.Where(a => !taken.Contains(a.App)).OrderBy(a => a.Title))
            box.Items.Add(new ComboBoxItem { Content = $"{a.Title} ({a.App})", Tag = a.App });
        box.Text = "";
    }

    void AddApp(ComboBox box, List<string> list)
    {
        var app = box.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : Rules.AppName(box.Text);
        if (app.Length > 0 && app != Rules.AllOthers && !list.Contains(app)) list.Add(app);
        ShowApps();
    }

    void ShowValues()
    {
        if (editing is null) return;
        ThresholdText.Text = string.Format(Loc.T("Rules.Dbfs"), editing.ThresholdDb.ToString("0").Replace("-", "−"));
        DepthText.Text = TrayMenu.FormatDb(-editing.DepthDb);
        BoostText.Text = editing.BoostTriggersDb > 0 ? TrayMenu.FormatDb(editing.BoostTriggersDb) : Loc.T("Boost.Off");
        Canvas.SetLeft(ThresholdLine, Math.Max(0, (editing.ThresholdDb - MeterFloorDb) / -MeterFloorDb * MeterBox.ActualWidth - 1.5));
    }

    /// <summary>The live level of the rule's triggers under the threshold line.</summary>
    void Tick()
    {
        if (editing is null) return;
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        var peak = s.Apps.Where(a => editing.Triggers.Contains(a.App)).Select(a => a.Peak).DefaultIfEmpty(0).Max();
        var level = Math.Clamp((Db.FromScalar(peak) - MeterFloorDb) / -MeterFloorDb, 0, 1);
        meter = Math.Max(level, meter - 0.06);
        TriggerMeter.Value = meter;
    }

    void Save()
    {
        var r = editing!;
        r.AttackMs = AttackBox.Value ?? r.AttackMs;
        r.HoldMs = HoldBox.Value ?? r.HoldMs;
        r.ReleaseMs = ReleaseBox.Value ?? r.ReleaseMs;
        r.MinSignalMs = MinSignalBox.Value ?? r.MinSignalMs;
        r.DepthDb = Math.Clamp(r.DepthDb, Rules.MinDepth, Rules.MaxDepth);
        var candidate = RuleSet.Select(x => x.Clone()).ToList();
        var i = editingIndex < 0 ? candidate.Count : editingIndex;
        if (editingIndex < 0) candidate.Add(r);
        else candidate[i] = r;

        // Errors next to the field they are about; other rules' old problems are not this editor's business
        var errors = Rules.Validate(candidate);
        ShowError(TriggerError, string.Join("\n", errors.Where(e => e.Rule == i && e.Problem == RuleProblem.NoTriggers || e.Rule != i && e.OtherRule == i + 1).Select(Message)));
        ShowError(TargetError, string.Join("\n", errors.Where(e => e.Rule == i && e.Problem != RuleProblem.NoTriggers).Select(Message)));
        if (TriggerError.Text.Length + TargetError.Text.Length > 0) return;

        if (editingIndex < 0) RuleSet.Add(r);
        else RuleSet[i] = r;
        Apply();
        Close();
    }

    static void ShowError(TextBlock t, string text)
    {
        t.Text = text;
        t.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static string Message(RuleError e)
    {
        var s = Engine.Current?.Snapshot ?? Snapshot.Empty;
        return e.Problem switch
        {
            RuleProblem.NoTriggers => Loc.T("Rules.Err.NoTriggers"),
            RuleProblem.NoTargets => Loc.T("Rules.Err.NoTargets"),
            _ when e.OtherRule == e.Rule + 1 => string.Format(Loc.T("Rules.Err.Both"), AppTitle(e.App!, s)),
            _ => string.Format(Loc.T("Rules.Err.Loop"), AppTitle(e.App!, s), e.OtherRule, e.Rule + 1),
        };
    }
}
