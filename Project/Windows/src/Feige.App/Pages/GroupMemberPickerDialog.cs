using Feige.App.Services;
using Feige.Core.Conversations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Feige.App.Pages;

/// <summary>Two-column picker shared by group creation and adding members (see UISpecification §4).</summary>
internal sealed class GroupMemberPickerDialog
{
    private readonly IReadOnlyList<GroupMemberPickerCandidate> _candidates;
    private readonly Func<string?> _blockingReason;
    private readonly bool _isCreate;
    private readonly GroupMemberPickerSelection _selection;
    private readonly ContentDialog _dialog;
    private readonly TextBox _search = new() { PlaceholderText = "搜索设备名称" };
    private readonly StackPanel _candidatePanel = new() { Spacing = 2 };
    private readonly TextBlock _selectedHeader = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly StackPanel _selectedPanel = new() { Spacing = 6 };
    private readonly TextBox _name = new() { Header = "群名称", Text = "新群组", MaxLength = 80 };
    private readonly TextBlock _blocking = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

    private GroupMemberPickerDialog(string? groupName, int existingMemberCount,
        IReadOnlyList<GroupMemberPickerCandidate> candidates, Func<string?> blockingReason)
    {
        _isCreate = groupName is null;
        _candidates = candidates;
        _blockingReason = blockingReason;
        _selection = new GroupMemberPickerSelection(existingMemberCount);
        _dialog = new ContentDialog
        {
            Title = _isCreate ? "选择成员" : $"选择成员 · 添加到“{groupName}”",
            Content = BuildLayout(),
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        _dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        AutomationProperties.SetName(_search, "搜索可邀请设备");
        _search.TextChanged += (_, _) => RenderCandidates();
        _name.TextChanged += (_, _) => RenderState();
        Render();
    }

    /// <summary>Shows the picker; returns the group name (create mode) and the devices to invite, or null when cancelled.</summary>
    public static async Task<(string Name, IReadOnlyList<GroupMemberPickerCandidate> Selected)?> ShowAsync(string? groupName,
        int existingMemberCount, IReadOnlyList<GroupMemberPickerCandidate> candidates, Func<string?>? blockingReason = null)
    {
        var picker = new GroupMemberPickerDialog(groupName, existingMemberCount, candidates, blockingReason ?? (() => null));
        if (await UiPromptService.Current.ShowAsync(picker._dialog) != ContentDialogResult.Primary) return null;
        var confirmed = picker.Confirmed();
        return (picker._name.Text.Trim(), confirmed);
    }

    private IReadOnlyList<GroupMemberPickerCandidate> Confirmed() =>
        _selection.ConfirmedFingerprints(_candidates).Select(id => _candidates.First(c => c.Fingerprint == id)).ToArray();

    private Grid BuildLayout()
    {
        var root = new Grid { ColumnSpacing = 16, Width = 680, Height = 420 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new Grid { RowSpacing = 10 };
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        left.Children.Add(_search);
        var candidateScroll = new ScrollViewer { Content = _candidatePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(candidateScroll, 1);
        left.Children.Add(candidateScroll);
        root.Children.Add(left);

        var divider = new Border { Width = 1, Background = ThemeBrush("DividerStrokeColorDefaultBrush") };
        Grid.SetColumn(divider, 1);
        root.Children.Add(divider);

        var right = new Grid { RowSpacing = 10 };
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.Children.Add(_selectedHeader);
        var selectedScroll = new ScrollViewer { Content = _selectedPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(selectedScroll, 1);
        right.Children.Add(selectedScroll);
        var footer = new StackPanel { Spacing = 8 };
        if (_isCreate) footer.Children.Add(_name);
        footer.Children.Add(new TextBlock
        {
            Text = $"对方接受邀请、全部成员确认后才正式加入；群聊最多 {GroupConversation.MaximumMembers} 人（含本机）。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });
        _blocking.Foreground = ThemeBrush("SystemFillColorCautionBrush");
        footer.Children.Add(_blocking);
        Grid.SetRow(footer, 2);
        right.Children.Add(footer);
        Grid.SetColumn(right, 2);
        root.Children.Add(right);
        return root;
    }

    private void Render()
    {
        RenderCandidates();
        RenderState();
    }

    private void RenderCandidates()
    {
        _candidatePanel.Children.Clear();
        var visible = GroupMemberPickerSelection.Visible(_candidates, _search.Text);
        if (visible.Count == 0)
        {
            _candidatePanel.Children.Add(Hint(_candidates.Count == 0 ? "还没有可选择的设备。先与对方建立连接后再邀请。" : "没有匹配的设备。"));
            return;
        }
        foreach (var candidate in visible) _candidatePanel.Children.Add(CandidateRow(candidate));
    }

    private UIElement CandidateRow(GroupMemberPickerCandidate candidate)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(4, 4, 4, 4), Opacity = candidate.IsDimmed ? 0.45 : 1 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var check = new CheckBox
        {
            IsChecked = _selection.IsSelected(candidate), IsEnabled = _selection.CanToggle(candidate),
            MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(check, $"{candidate.Name}，{candidate.StatusText}");
        check.Click += (_, _) => { _selection.Toggle(candidate); Render(); };
        row.Children.Add(check);
        var avatar = Avatar(candidate.Fingerprint, 32);
        Grid.SetColumn(avatar, 1);
        row.Children.Add(avatar);
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = candidate.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        labels.Children.Add(new TextBlock { Text = candidate.StatusText, FontSize = 11, Foreground = ThemeBrush("TextFillColorSecondaryBrush") });
        Grid.SetColumn(labels, 2);
        row.Children.Add(labels);
        return row;
    }

    private void RenderState()
    {
        var confirmed = Confirmed();
        _selectedHeader.Text = $"已选择 {confirmed.Count} · 还可添加 {_selection.Remaining}";
        _selectedPanel.Children.Clear();
        if (confirmed.Count == 0) _selectedPanel.Children.Add(Hint("在左侧勾选要邀请的设备。"));
        foreach (var candidate in confirmed) _selectedPanel.Children.Add(SelectedChip(candidate));

        string? reason = _blockingReason();
        _blocking.Text = reason ?? "";
        _blocking.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        _dialog.PrimaryButtonText = _isCreate
            ? confirmed.Count == 0 ? "创建并邀请" : $"创建并邀请 {confirmed.Count} 台设备"
            : confirmed.Count == 0 ? "邀请" : $"邀请 {confirmed.Count} 台设备";
        _dialog.IsPrimaryButtonEnabled = confirmed.Count > 0 && reason is null && (!_isCreate || !string.IsNullOrWhiteSpace(_name.Text));
    }

    private UIElement SelectedChip(GroupMemberPickerCandidate candidate)
    {
        var chip = new Grid { ColumnSpacing = 8, Padding = new Thickness(8, 4, 4, 4), CornerRadius = new CornerRadius(6), Background = ThemeBrush("SubtleFillColorSecondaryBrush") };
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        chip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        chip.Children.Add(Avatar(candidate.Fingerprint, 24));
        var name = new TextBlock { Text = candidate.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(name, 1);
        chip.Children.Add(name);
        var remove = new Button { Content = new SymbolIcon(Symbol.Cancel), Padding = new Thickness(6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(remove, $"移除 {candidate.Name}");
        remove.Click += (_, _) => { _selection.Remove(candidate.Fingerprint); Render(); };
        Grid.SetColumn(remove, 2);
        chip.Children.Add(remove);
        return chip;
    }

    private static Image Avatar(string fingerprint, double size) => new()
    {
        Source = AvatarCatalog.GetImageSource(AppSession.Current.Settings.GetPeerAvatarId(fingerprint)),
        Width = size, Height = size, Stretch = Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Hint(string text) =>
        new() { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = ThemeBrush("TextFillColorSecondaryBrush"), Margin = new Thickness(0, 8, 0, 0) };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.Resources[key];
}
