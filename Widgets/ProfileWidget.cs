using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QiDock.Models;
using QiDock.Services;

namespace QiDock.Widgets;

/// <summary>
/// The active widget profile ("İş", "Kod"…) as a tile; a click opens the profiles card (switch, new, rename, remove,
/// edit this profile's widgets). The card opens like every other widget's card: it grows out of the dock.
/// </summary>
public sealed class ProfileWidget : DockWidget
{
    /// <summary>Preset names offered for a new profile or a rename.</summary>
    private static readonly string[] PresetNames = ["ProfileWork", "ProfileCode", "ProfileHome", "ProfileFocus", "ProfileGame", "ProfileSchool", "ProfileCreative", "ProfileTools", "ProfileDefault"];

    private readonly Button _tile;
    private readonly TextBlock _name;
    private readonly TextBlock _caption;
    private readonly WidgetPopup _popup;

    /// <summary>Which name list is unfolded in the card: none, the new-profile names or the rename names.</summary>
    private enum Fold { None, Create, Rename }
    private Fold _fold;

    /// <summary>Set by the bar: whether its widgets are being arranged, and the switch for it.</summary>
    internal Func<bool>? IsEditing { get; set; }
    internal Action? ToggleEditing { get; set; }

    public ProfileWidget()
    {
        var icon = Ui.Icon("IconPerson", 15);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _caption = Ui.Text("", 10, brushKey: "TextSecondary");
        _name = Ui.Text("", 12.5, FontWeights.SemiBold);
        _name.MaxWidth = 110;
        text.Children.Add(_caption);
        text.Children.Add(_name);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        _tile = Ui.Tile(row, (_, _) => Toggle());
        _popup = new WidgetPopup(_tile);
        _popup.Closed += (_, _) => _fold = Fold.None;
        UiText.Current.PropertyChanged += (_, _) => Render();
        Render();
    }

    public override string Id => WidgetIds.Profile;
    public override FrameworkElement View => _tile;
    public override bool IsPopupOpen => _popup.IsOpen;
    public override void ClosePopup() => _popup.Close();

    public override void Stop() => _popup.Close();

    public override void ApplySettings(WidgetSettings settings)
    {
        Render();
        if (_popup.IsOpen) BuildCard();
    }

    private void Render()
    {
        var profiles = Current.EffectiveProfiles;
        _caption.Text = UiText.Get("ProfileCaption");
        _name.Text = profiles[Current.EffectiveActiveProfile].Name;
        _tile.ToolTip = _popup?.IsOpen == true ? null : UiText.Get("ProfileTileHint");
    }

    private void Toggle()
    {
        if (!_popup.IsOpen)
        {
            _fold = Fold.None;
            BuildCard();
        }
        _popup.Toggle();
    }

    private void BuildCard()
    {
        var settings = Current;
        var profiles = settings.EffectiveProfiles;
        var active = settings.EffectiveActiveProfile;
        var root = new StackPanel();
        root.Children.Add(Ui.Header(UiText.Get("ProfilesTitle"), _popup.Close));

        for (var i = 0; i < profiles.Count; i++)
        {
            var index = i;
            var line = new DockPanel();
            if (index == active)
            {
                var check = Ui.Icon("IconCheck", 13, brushKey: "WidgetAccent", stroke: 2.4);
                check.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(check, Dock.Right);
                line.Children.Add(check);
            }
            if (index < 9)
            {
                var keys = Ui.Text(UiText.Format("ProfileShortcut", index + 1), 10.5, brushKey: "TextSecondary");
                keys.Margin = new Thickness(10, 0, 0, 0);
                DockPanel.SetDock(keys, Dock.Right);
                line.Children.Add(keys);
            }
            line.Children.Add(Ui.Text(profiles[index].Name, 13, index == active ? FontWeights.SemiBold : FontWeights.Normal));
            root.Children.Add(Row(line, () =>
            {
                _popup.Close();
                if (index != Current.EffectiveActiveProfile) UpdateSettings(s => s.SwitchProfile(index));
                Ui.ShowHud("IconPerson", profiles[index].Name);
            }));
        }

        root.Children.Add(Divider());
        var taken = profiles.Select(p => p.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        root.Children.Add(Row(Action("IconPlus", UiText.Get("ProfileNewMenu"), _fold == Fold.Create), () => Unfold(Fold.Create)));
        if (_fold == Fold.Create)
        {
            var names = PresetNames.Select(UiText.Get).Where(name => !taken.Contains(name))
                .Append(UiText.Format("ProfileNew", profiles.Count + 1));
            root.Children.Add(Chips(names.Select(name => (name, true, false)), name =>
            {
                _popup.Close();
                UpdateSettings(s => s.AddProfile(name));
                Ui.ShowHud("IconPerson", UiText.Format("ProfileCreated", name));
            }));
        }

        root.Children.Add(Row(Action("IconEdit", UiText.Get("ProfileRename"), _fold == Fold.Rename), () => Unfold(Fold.Rename)));
        if (_fold == Fold.Rename)
        {
            var current = profiles[active].Name;
            root.Children.Add(Chips(PresetNames.Select(UiText.Get).Select(name => (name, !taken.Contains(name) || name == current, name == current)), name =>
            {
                _popup.Close();
                UpdateSettings(s => s.RenameProfile(active, name));
            }));
        }

        var remove = Row(Action("IconClose", UiText.Format("ProfileRemoveNamed", profiles[active].Name), null), () =>
        {
            _popup.Close();
            UpdateSettings(s => s.RemoveProfile(active));
        });
        remove.IsEnabled = profiles.Count > 1;
        root.Children.Add(remove);

        root.Children.Add(Divider());
        var editing = IsEditing?.Invoke() == true;
        root.Children.Add(Row(Action("IconLayout", UiText.Get(editing ? "MenuEditDone" : "ProfileEditWidgets"), null), () =>
        {
            _popup.Close();
            ToggleEditing?.Invoke();
        }));

        _popup.SetContent(root, 300);
    }

    private void Unfold(Fold fold)
    {
        _fold = _fold == fold ? Fold.None : fold;
        BuildCard();
    }

    private static Button Row(UIElement content, Action click)
    {
        var button = new Button { Content = content };
        button.SetResourceReference(FrameworkElement.StyleProperty, "WidgetRowButton");
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>An action line: icon, label and, for the ones that unfold a name list, a chevron that turns.</summary>
    private static DockPanel Action(string iconKey, string label, bool? unfolded)
    {
        var line = new DockPanel();
        var icon = Ui.Icon(iconKey, 14);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        line.Children.Add(icon);
        if (unfolded is { } open)
        {
            var chevron = Ui.Icon("IconChevronRight", 12, brushKey: "TextSecondary");
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = new RotateTransform(open ? 90 : 0);
            DockPanel.SetDock(chevron, Dock.Right);
            line.Children.Add(chevron);
        }
        line.Children.Add(Ui.Text(label, 13));
        return line;
    }

    /// <summary>The unfolded names as small pills under their action; the current one is marked, taken ones are dimmed.</summary>
    private static WrapPanel Chips(IEnumerable<(string Name, bool Enabled, bool Current)> names, Action<string> pick)
    {
        var panel = new WrapPanel { Margin = new Thickness(34, 2, 0, 6) };
        foreach (var (name, enabled, current) in names)
        {
            var chip = Ui.Pill(name, (_, _) => pick(name), current ? "IconCheck" : null);
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.IsEnabled = enabled && !current;
            if (!enabled) chip.Opacity = 0.4;
            panel.Children.Add(chip);
        }
        return panel;
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(4, 6, 4, 6) };
        line.SetResourceReference(Border.BackgroundProperty, "MenuStroke");
        return line;
    }
}
