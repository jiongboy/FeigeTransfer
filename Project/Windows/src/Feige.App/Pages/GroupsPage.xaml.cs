using System.Collections.Specialized;
using Feige.App.Services;
using Feige.Core.Conversations;
using Feige.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Feige.App.Pages;

public sealed partial class GroupsPage : Page
{
    private V4GroupAppService? Service => AppSession.Current.Groups;
    private GroupConversation? _selected;
    private Guid? _selectedGroupId;
    private V4GroupAppService? _observedService;
    private bool _refreshing;
    private bool _suppressSelectionChanged;
    private bool _isChoosingMembers;
    private bool _membershipChangeInFlight;
    private bool _showGroupListInNarrow;
    private bool _showMembersInNarrow;
    private readonly HashSet<Guid> _leaveRequestsInFlight = [];

    public GroupsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        GroupList.ItemsSource = AppSession.Current.Groups?.Groups;
        GroupList.ItemTemplate = GroupTemplate();
        AvailableMemberList.ItemTemplate = PeerTemplate();
        MessageList.ItemsSource = null;
        Loaded += (_, _) => RefreshResponsiveLayout(Root.ActualWidth);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e) => RefreshResponsiveLayout(e.NewSize.Width);

    private void RefreshResponsiveLayout(double width)
    {
        bool narrow = width < 980;
        if (!narrow) { _showGroupListInNarrow = false; _showMembersInNarrow = false; }
        if (_selected is null) _showMembersInNarrow = false;
        bool showGroupList = narrow && _showGroupListInNarrow;
        bool showMemberInspector = narrow && _showMembersInNarrow && _selected is not null;
        bool hasDetail = _selected is not null || Service?.PendingApprovals.Count > 0;

        ListColumn.Width = narrow
            ? showGroupList ? new GridLength(1, GridUnitType.Star) : new GridLength(0)
            : new GridLength(280);
        DetailColumn.Width = narrow
            ? showGroupList ? new GridLength(0) : new GridLength(1, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        GroupList.Visibility = !narrow || showGroupList ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = hasDetail && !showGroupList ? Visibility.Visible : Visibility.Collapsed;
        EmptyGroups.Visibility = !hasDetail && !showGroupList ? Visibility.Visible : Visibility.Collapsed;

        ChatColumn.Width = narrow
            ? showMemberInspector ? new GridLength(0) : new GridLength(1, GridUnitType.Star)
            : new GridLength(1, GridUnitType.Star);
        MemberInspectorColumn.Width = narrow
            ? showMemberInspector ? new GridLength(1, GridUnitType.Star) : new GridLength(0)
            : _selected is not null ? new GridLength(310) : new GridLength(0);
        ChatPane.Visibility = showMemberInspector ? Visibility.Collapsed : Visibility.Visible;
        MemberInspector.Visibility = _selected is not null && (!narrow || showMemberInspector)
            ? Visibility.Visible : Visibility.Collapsed;
        ComposerGrid.Visibility = showMemberInspector ? Visibility.Collapsed : Visibility.Visible;
        ShowGroupsButton.Visibility = narrow && !showGroupList && _selected is not null
            ? Visibility.Visible : Visibility.Collapsed;
        MembersButton.Visibility = narrow && !showMemberInspector && _selected is not null
            ? Visibility.Visible : Visibility.Collapsed;
        CloseMemberInspectorButton.Visibility = showMemberInspector ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShowGroupsClick(object sender, RoutedEventArgs e)
    {
        _showMembersInNarrow = false;
        _showGroupListInNarrow = true;
        RefreshResponsiveLayout(Root.ActualWidth);
    }

    private void OnMembersClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _showGroupListInNarrow = false;
        _showMembersInNarrow = true;
        RefreshResponsiveLayout(Root.ActualWidth);
    }

    private void OnCloseMemberInspectorClick(object sender, RoutedEventArgs e)
    {
        _showMembersInNarrow = false;
        RefreshResponsiveLayout(Root.ActualWidth);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var app = AppSession.Current;
        app.StateChanged += OnAppChanged;
        app.Peers.CollectionChanged += OnPeersChanged;
        app.Conversations.CollectionChanged += OnPeersChanged;
        if (app.Groups is { } service) ObserveService(service);
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        var app = AppSession.Current;
        app.StateChanged -= OnAppChanged;
        app.Peers.CollectionChanged -= OnPeersChanged;
        app.Conversations.CollectionChanged -= OnPeersChanged;
        ObserveService(null);
        app.SetVisibleConversation(null);
        base.OnNavigatedFrom(e);
    }

    private void OnAppChanged(object? sender, EventArgs e) => Refresh();
    private void OnPeersChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();
    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Guid? selectedId = _selected?.Id ?? _selectedGroupId;
        _suppressSelectionChanged = true;
        try
        {
            Refresh();
            if (selectedId is { } id && Service?.Groups.FirstOrDefault(group => group.Id == id) is { } selected)
            {
                _selected = selected;
                _selectedGroupId = id;
                GroupList.SelectedItem = selected;
                RefreshSelection();
            }
        }
        finally
        {
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    if (!DispatcherQueue.TryEnqueue(() =>
                        {
                            if (selectedId is { } id &&
                                Service?.Groups.FirstOrDefault(group => group.Id == id) is { } selected)
                            {
                                _selected = selected;
                                _selectedGroupId = id;
                                GroupList.SelectedItem = selected;
                                RefreshSelection();
                            }
                            if (!DispatcherQueue.TryEnqueue(() => _suppressSelectionChanged = false))
                                _suppressSelectionChanged = false;
                        }))
                        _suppressSelectionChanged = false;
                }))
                _suppressSelectionChanged = false;
        }
    }
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshMessages();
    private void OnApprovalsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshApprovals();
        RefreshDetailPaneVisibility();
    }
    private void OnOperationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshOperations();
    private void OnTransactionFailed(object? sender, string error) => SetStatus(error, InfoBarSeverity.Error);

    private void ObserveService(V4GroupAppService? service)
    {
        if (ReferenceEquals(_observedService, service)) return;
        if (_observedService is { } previous)
        {
            previous.Groups.CollectionChanged -= OnGroupsChanged;
            previous.Messages.CollectionChanged -= OnMessagesChanged;
            previous.PendingApprovals.CollectionChanged -= OnApprovalsChanged;
            previous.Operations.CollectionChanged -= OnOperationsChanged;
            previous.TransactionFailed -= OnTransactionFailed;
        }
        _observedService = service;
        if (service is not null)
        {
            service.Groups.CollectionChanged += OnGroupsChanged;
            service.Messages.CollectionChanged += OnMessagesChanged;
            service.PendingApprovals.CollectionChanged += OnApprovalsChanged;
            service.Operations.CollectionChanged += OnOperationsChanged;
            service.TransactionFailed += OnTransactionFailed;
        }
    }

    private void Refresh()
    {
        if (_refreshing) return;
        ObserveService(Service);
        _refreshing = true;
        try
        {
            var groups = Service?.Groups;
            if (groups is not null)
            {
                Guid? selectedId = _selected?.Id ?? _selectedGroupId;
                if (!ReferenceEquals(GroupList.ItemsSource, groups)) GroupList.ItemsSource = groups;
                _selected = selectedId is { } id ? groups.FirstOrDefault(g => g.Id == id) : null;
                if (_selected is null && groups.Count > 0) _selected = groups[0];
                if (_selected is not null) _selectedGroupId = _selected.Id;
                GroupList.SelectedItem = _selected;
            }
            bool available = Service is not null;
            CreateButton.IsEnabled = available && AppSession.Current.Conversations.Any(IsEligibleConversation);
            if (_selected is not null) RefreshSelection();
            RefreshApprovals();
            RefreshDetailPaneVisibility();
            RefreshOperations();
        }
        finally { _refreshing = false; }
    }

    private void RefreshDetailPaneVisibility()
    {
        bool hasSelection = _selected is not null;
        bool hasApprovals = Service?.PendingApprovals.Count > 0;
        if (!hasSelection)
        {
            _showMembersInNarrow = false;
            GroupTitle.Text = hasApprovals ? "待处理请求" : "选择群组";
            MemberGroupName.Text = "";
            MemberCount.Text = "";
            MemberPanel.Children.Clear();
            MessageList.ItemsSource = null;
            AddMemberButton.IsEnabled = false;
            LeaveButton.IsEnabled = false;
            PickFileButton.IsEnabled = false;
            AvailableMemberList.Visibility = Visibility.Collapsed;
            AddMemberExplanation.Visibility = Visibility.Collapsed;
            AvailableMembersEmpty.Visibility = Visibility.Collapsed;
            AddMemberActions.Visibility = Visibility.Collapsed;
            Composer.IsEnabled = false;
            SendButton.IsEnabled = false;
        }
        RefreshResponsiveLayout(Root.ActualWidth);
    }

    private static bool IsEligibleConversation(ConversationItem conversation)
        => !string.IsNullOrWhiteSpace(conversation.Fingerprint) &&
           AppSession.Current.Groups?.IsPeerConnected(conversation.Fingerprint) == true;

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _suppressSelectionChanged) return;
        GroupConversation? selected = GroupList.SelectedItem as GroupConversation;
        if (selected is not null) AppSession.Current.SetVisibleConversation(UnreadCounter.GroupKey(selected.Id));
        if (selected is null && _selectedGroupId is { } retainedId &&
            Service?.Groups.Any(group => group.Id == retainedId) == true)
        {
            _suppressSelectionChanged = true;
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    if (!DispatcherQueue.TryEnqueue(() =>
                        {
                            if (Service?.Groups.FirstOrDefault(group => group.Id == retainedId) is { } retained)
                            {
                                _selected = retained;
                                _selectedGroupId = retainedId;
                                GroupList.SelectedItem = retained;
                                RefreshSelection();
                                if (!DispatcherQueue.TryEnqueue(() => _suppressSelectionChanged = false))
                                    _suppressSelectionChanged = false;
                            }
                            else _suppressSelectionChanged = false;
                        }))
                        _suppressSelectionChanged = false;
                }))
                _suppressSelectionChanged = false;
            return;
        }
        if (_selected?.Id != selected?.Id)
        {
            _isChoosingMembers = false;
            _showGroupListInNarrow = false;
            _showMembersInNarrow = false;
            MemberSearch.Text = "";
        }
        _selected = selected;
        _selectedGroupId = _selected?.Id;
        RefreshDetailPaneVisibility();
        if (_selected is not null) RefreshSelection();
    }

    private void OnGroupItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GroupConversation group) return;
        _isChoosingMembers = false;
        _showGroupListInNarrow = false;
        _showMembersInNarrow = false;
        _selected = group;
        _selectedGroupId = group.Id;
        _suppressSelectionChanged = true;
        GroupList.SelectedItem = group;
        _suppressSelectionChanged = false;
        RefreshDetailPaneVisibility();
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_selected is null || Service is null) return;
        var group = _selected;
        var app = AppSession.Current;
        string? localFingerprint = app.Identity?.Fingerprint;
        bool localMember = group.Members.Any(member => member.Fingerprint == localFingerprint);
        bool hasRemoteMember = group.Members.Any(member => member.Fingerprint != localFingerprint);
        bool owner = group.OwnerFingerprint == localFingerprint;
        bool allMembersOnline = group.Members.All(member => member.Fingerprint == localFingerprint || Service.IsPeerConnected(member.Fingerprint));
        bool hasPendingTransaction = _membershipChangeInFlight || Service.Operations.Any(op => op.GroupID == group.Id && op.Pending) ||
            Service.Store.PendingMembershipPrepares.Any(prepare => prepare.GroupID == group.Id);

        GroupTitle.Text = group.Name;
        MemberGroupName.Text = group.Name;
        MemberCount.Text = $"{group.Members.Count}/{GroupConversation.MaximumMembers} 位成员";
        MemberPanel.Children.Clear();
        var visibleMembers = group.Members.Where(member => string.IsNullOrWhiteSpace(MemberSearch.Text) ||
            (member.DisplayName?.Contains(MemberSearch.Text, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
            member.Fingerprint.Contains(MemberSearch.Text, StringComparison.OrdinalIgnoreCase) ||
            (member.Fingerprint == localFingerprint && "本机".Contains(MemberSearch.Text, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        foreach (var member in visibleMembers) MemberPanel.Children.Add(BuildMemberRow(group, member, owner, allMembersOnline, hasPendingTransaction));
        if (visibleMembers.Length == 0)
            MemberPanel.Children.Add(new TextBlock { Text = "没有匹配的成员。", FontSize = 12, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });

        AddMemberButton.IsEnabled = owner && group.Members.Count < GroupConversation.MaximumMembers && allMembersOnline && !hasPendingTransaction;
        LeaveButton.Content = owner ? (group.Members.Count == 1 ? "结束群聊" : "退出并转让群主") : "申请退出群聊";
        LeaveButton.IsEnabled = localMember && !hasPendingTransaction &&
            (owner ? allMembersOnline : (Service.IsPeerConnected(group.OwnerFingerprint) && !_leaveRequestsInFlight.Contains(group.Id)));
        PickFileButton.IsEnabled = localMember && group.Members.Any(member => member.Fingerprint != localFingerprint && Service.IsPeerConnected(member.Fingerprint));
        Composer.IsEnabled = localMember && hasRemoteMember;
        SendButton.IsEnabled = localMember && hasRemoteMember;
        MemberConstraint.Text = allMembersOnline
            ? "成员名单变更需要所有成员在线并明确确认；群聊最多 20 人（含本机）。"
            : "有成员离线。添加或移除成员、群主转让暂不可用；请等待自动重连后再操作。";
        MessageList.ItemsSource = Service.Messages.Where(m => m.GroupID == group.Id).OrderBy(m => m.Date).Select(BuildMessageRow).ToArray();
        RefreshAvailableMembers(group, allMembersOnline, hasPendingTransaction);
    }

    private StackPanel BuildMemberRow(GroupConversation group, GroupMember member, bool owner, bool allMembersOnline, bool hasPendingTransaction)
    {
        var app = AppSession.Current;
        bool isLocal = member.Fingerprint == app.Identity?.Fingerprint;
        bool online = isLocal || Service?.IsPeerConnected(member.Fingerprint) == true;
        string? avatarId = isLocal ? app.Settings.AvatarId : app.Settings.GetPeerAvatarId(member.Fingerprint);
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var avatar = new Image { Source = AvatarCatalog.GetImageSource(avatarId), Width = 36, Height = 36, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(avatar, 0);
        row.Children.Add(avatar);
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(member.DisplayName) ? "未知设备" : member.DisplayName, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        string role = member.Fingerprint == group.OwnerFingerprint ? "群主" : "成员";
        labels.Children.Add(new TextBlock { Text = isLocal ? $"本机 · {role}" : $"{role} · {(online ? "在线" : "离线")}", FontSize = 11, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        Grid.SetColumn(labels, 1);
        row.Children.Add(labels);
        if (owner && !isLocal && member.Fingerprint != group.OwnerFingerprint)
        {
            var remove = new Button { Content = "移出群聊", Padding = new Thickness(8, 4, 8, 4), IsEnabled = allMembersOnline && !hasPendingTransaction };
            AutomationProperties.SetName(remove, $"移出成员 {member.DisplayName ?? "未知设备"}");
            remove.Click += async (_, _) => await RemoveMemberAsync(group, member);
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
        }
        return new StackPanel { Children = { row } };
    }

    private void RefreshAvailableMembers(GroupConversation group, bool allMembersOnline, bool hasPendingTransaction)
    {
        var candidates = AppSession.Current.Conversations.Where(IsEligibleConversation)
            .Where(conversation => !group.Members.Any(member => member.Fingerprint == conversation.Fingerprint))
            .Where(conversation => string.IsNullOrWhiteSpace(MemberSearch.Text) ||
                conversation.Name.Contains(MemberSearch.Text, StringComparison.CurrentCultureIgnoreCase) ||
                conversation.Fingerprint.Contains(MemberSearch.Text, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        AvailableMemberList.ItemsSource = candidates;
        AvailableMemberList.Visibility = _isChoosingMembers && candidates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddMemberExplanation.Visibility = _isChoosingMembers && group.OwnerFingerprint == AppSession.Current.Identity?.Fingerprint ? Visibility.Visible : Visibility.Collapsed;
        AvailableMembersEmpty.Visibility = _isChoosingMembers && candidates.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddMemberActions.Visibility = _isChoosingMembers ? Visibility.Visible : Visibility.Collapsed;
        AddMemberButton.IsEnabled = !_isChoosingMembers && group.OwnerFingerprint == AppSession.Current.Identity?.Fingerprint &&
            group.Members.Count < GroupConversation.MaximumMembers && allMembersOnline && !hasPendingTransaction && candidates.Length > 0 && !_membershipChangeInFlight;
        MemberConstraint.Text = !allMembersOnline
            ? "有成员离线。添加或移除成员、群主转让暂不可用；请等待自动重连后再操作。"
            : group.Members.Count >= GroupConversation.MaximumMembers
                ? "群聊已达到 20 人上限；如需添加成员，请先移除一位成员。"
                : "成员名单变更需要所有成员在线并明确确认；群聊最多 20 人（含本机）。";
    }

    private async void OnMemberSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_selected is not null) RefreshSelection();
    }

    private StackPanel BuildMessageRow(GroupMessageEnvelope message)
    {
        var row = new StackPanel { Spacing = 4, Margin = new Thickness(4, 8, 4, 8) };
        string? localFingerprint = AppSession.Current.Identity?.Fingerprint;
        bool isLocal = message.SenderFingerprint == localFingerprint;
        string sender = isLocal ? "我" : _selected?.Members.FirstOrDefault(m => m.Fingerprint == message.SenderFingerprint)?.DisplayName ?? message.SenderFingerprint;
        string? avatarId = isLocal ? AppSession.Current.Settings.AvatarId : AppSession.Current.Settings.GetPeerAvatarId(message.SenderFingerprint);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new Image { Source = AvatarCatalog.GetImageSource(avatarId), Width = 24, Height = 24, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
        header.Children.Add(new TextBlock { Text = $"{sender} · {message.Date.ToLocalTime():yyyy-MM-dd HH:mm}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(header);
        row.Children.Add(new TextBlock { Text = message.Kind == GroupMessageKind.Text ? message.Text ?? "" : $"{(message.Kind == GroupMessageKind.Image ? "图片" : "文件")}: {message.FileMetadata?.FileName}", TextWrapping = TextWrapping.Wrap, FontSize = 14, Margin = new Thickness(31, 0, 0, 0) });
        return row;
    }

    private void RefreshMessages() { if (_selected is not null) RefreshSelection(); }

    private void RefreshApprovals()
    {
        if (Service is null) return;
        var pendingKeys = Service.PendingApprovals.Select(a => "Approval:" + a.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in OperationPanel.Children.OfType<FrameworkElement>()
                     .Where(e => e.Tag is string tag && tag.StartsWith("Approval:", StringComparison.Ordinal) && !pendingKeys.Contains(tag)).ToArray())
            OperationPanel.Children.Remove(stale);
        foreach (var approval in Service.PendingApprovals.ToArray())
        {
            // Request remains visible in the service; a one-time card is keyed by the request id.
            string key = "Approval:" + approval.Id;
            if (OperationPanel.Children.OfType<FrameworkElement>().Any(e => Equals(e.Tag, key))) continue;
            var card = new StackPanel { Spacing = 5, Margin = new Thickness(0, 5, 0, 5), Tag = key };
            string description = approval.Invitation is { } invite
                ? $"{approval.PeerName} 邀请你加入“{invite.Name}”"
                : approval.LeaveRequest is { } leave
                    ? $"{approval.PeerName} 申请退出群聊“{Service.Store.GetGroup(leave.GroupID)?.Name ?? "群聊"}”"
                    : $"{approval.PeerName} 请求发送文件“{approval.File?.Metadata.FileName}”";
            var identityRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            identityRow.Children.Add(new Image { Source = AvatarCatalog.GetImageSource(AppSession.Current.Settings.GetPeerAvatarId(approval.PeerFingerprint)), Width = 34, Height = 34, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
            identityRow.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            card.Children.Add(identityRow);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var accept = new Button { Content = approval.LeaveRequest is null ? "接受" : "批准退出" }; accept.Click += (_, _) => Decide(approval.Id, true, card);
            var decline = new Button { Content = "拒绝" }; decline.Click += (_, _) => Decide(approval.Id, false, card);
            actions.Children.Add(accept); actions.Children.Add(decline); card.Children.Add(actions);
            OperationPanel.Children.Insert(0, card);
        }
    }

    private void Decide(Guid id, bool accept, FrameworkElement card)
    {
        if (Service?.Decide(id, accept) == true) OperationPanel.Children.Remove(card);
        else SetStatus("该请求已超时或已处理，决定未提交。", InfoBarSeverity.Warning);
    }

    private void RefreshOperations()
    {
        if (Service is null) return;
        foreach (var stale in OperationPanel.Children.OfType<FrameworkElement>().Where(e => e.Tag is string tag && tag.StartsWith("Operation:", StringComparison.Ordinal)).ToArray())
            OperationPanel.Children.Remove(stale);
        foreach (var op in Service.Operations.Where(op => _selected is null || op.GroupID == _selected.Id))
        {
            var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4), Tag = "Operation:" + op.GroupID };
            string stageText = op.Stage switch
            {
                V4GroupMembershipStage.Preparing => "成员变更等待成员确认",
                V4GroupMembershipStage.Committing => "成员名单提交待完成",
                V4GroupMembershipStage.Aborting => "取消成员变更待确认",
                _ => "成员事务状态未知"
            };
            box.Children.Add(new TextBlock { Text = op.Pending ? stageText : "成员事务存在错误" , VerticalAlignment = VerticalAlignment.Center });
            if (!string.IsNullOrWhiteSpace(op.Error)) box.Children.Add(new TextBlock { Text = op.Error, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
            if (op.Pending)
            {
                var retry = new Button { Content = "重试事务" }; retry.Click += async (_, _) => { try { await Service.RetryPendingTransactionAsync(op.GroupID); } catch (Exception ex) { SetStatus("事务仍未完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); } };
                box.Children.Add(retry);
                if (op.Stage == V4GroupMembershipStage.Preparing)
                {
                    var cancel = new Button { Content = "取消变更" };
                    cancel.Click += async (_, _) =>
                    {
                        try { await Service.CancelPendingTransactionAsync(op.GroupID); }
                        catch (Exception ex) { SetStatus("取消仍待确认：" + ErrorText.Describe(ex), InfoBarSeverity.Warning); }
                    };
                    box.Children.Add(cancel);
                }
            }
            OperationPanel.Children.Add(box);
        }
    }

    private async void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var candidates = AppSession.Current.Conversations.Where(IsEligibleConversation).ToArray();
        if (candidates.Length == 0) { SetStatus("没有已连接的 v4 设备可加入群组。", InfoBarSeverity.Warning); return; }
        var name = new TextBox { Header = "群组名称", Text = "新群组", MaxLength = 80 };
        var peers = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 280, ItemsSource = candidates };
        peers.ItemTemplate = PeerTemplate();
        var form = new StackPanel { Spacing = 10 }; form.Children.Add(name); form.Children.Add(new TextBlock { Text = "仅显示当前已有 v4 连接的设备；本机计入 20 人上限，最多选择 19 台。" }); form.Children.Add(peers);
        var dialog = new ContentDialog { Title = "创建群组", Content = form, PrimaryButtonText = "创建并邀请", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var selected = peers.SelectedItems.OfType<ConversationItem>().Where(IsEligibleConversation).Select(c => new GroupMember(c.Fingerprint, c.Name)).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("至少选择一台已连接的 v4 设备。");
            if (selected.Length > GroupConversation.MaximumMembers - 1) throw new InvalidOperationException("群组最多 20 人，包含本机；请最多选择 19 台设备。");
            await Service!.CreateGroupAsync(name.Text, selected);
            SetStatus("群组已创建，成员事务已完成。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { SetStatus("群组创建失败或尚未确认完成；若事务已写入，会保留在群组/事务状态中：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
        Refresh();
    }

    private DataTemplate GroupTemplate()
    {
        var xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Padding='10,9' Spacing='3'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='{Binding Members.Count, Mode=OneWay}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></StackPanel></DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private DataTemplate PeerTemplate()
    {
        var xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' Spacing='9' Padding='8,6'><Image Source='{Binding AvatarImage}' Width='34' Height='34' Stretch='UniformToFill'/><StackPanel VerticalAlignment='Center'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='已连接，可邀请' FontSize='10' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/></StackPanel></StackPanel></DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private void OnAddMemberClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _isChoosingMembers = true;
        RefreshSelection();
        if (AvailableMemberList.Items.Count == 0)
        {
            _isChoosingMembers = false;
            RefreshSelection();
            SetStatus("没有可添加的已连接设备。", InfoBarSeverity.Warning);
        }
    }

    private void OnCancelAddMembersClick(object sender, RoutedEventArgs e)
    {
        _isChoosingMembers = false;
        AvailableMemberList.SelectedItems.Clear();
        if (_selected is not null) RefreshSelection();
    }

    private async void OnApplyAddMembersClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || Service is null || _membershipChangeInFlight) return;
        var group = _selected;
        var selected = AvailableMemberList.SelectedItems.OfType<ConversationItem>().Where(IsEligibleConversation).ToArray();
        if (selected.Length == 0) { SetStatus("请选择至少一台已连接设备。", InfoBarSeverity.Warning); return; }
        if (group.Members.Count + selected.Length > GroupConversation.MaximumMembers)
        {
            SetStatus("群组最多 20 人，包含本机；请减少所选成员。", InfoBarSeverity.Warning);
            return;
        }
        if (!AllRemoteMembersConnected(group))
        {
            SetStatus("当前有群成员离线；成员名单变更暂不可用。", InfoBarSeverity.Warning);
            RefreshSelection();
            return;
        }
        var proposed = group.Members.Concat(selected.Select(conversation => new GroupMember(conversation.Fingerprint, conversation.Name))).ToArray();
        _membershipChangeInFlight = true;
        _isChoosingMembers = false;
        RefreshSelection();
        try
        {
            await Service.ChangeMembersAsync(group.Id, proposed);
            SetStatus("邀请已发送；成员确认和名单事务会保留在操作状态中。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { SetStatus("添加成员未确认完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
        finally
        {
            _membershipChangeInFlight = false;
            Refresh();
        }
    }

    private async Task RemoveMemberAsync(GroupConversation group, GroupMember member)
    {
        if (Service is null || _membershipChangeInFlight) return;
        var dialog = new ContentDialog
        {
            Title = "确认移出群聊？",
            Content = $"将“{member.DisplayName ?? member.Fingerprint[^8..]}”移出群聊。名单变更需要所有当前成员在线并明确确认。",
            PrimaryButtonText = "移出群聊",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (!AllRemoteMembersConnected(group))
        {
            SetStatus("当前有群成员离线；移除操作已取消。", InfoBarSeverity.Warning);
            RefreshSelection();
            return;
        }
        _membershipChangeInFlight = true;
        RefreshSelection();
        try
        {
            await Service.ChangeMembersAsync(group.Id, group.Members.Where(existing => existing.Fingerprint != member.Fingerprint).ToArray());
            SetStatus("移除请求已提交；成员确认和名单事务会保留在操作状态中。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { SetStatus("移出成员未确认完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
        finally
        {
            _membershipChangeInFlight = false;
            Refresh();
        }
    }

    private bool AllRemoteMembersConnected(GroupConversation group)
    {
        string? localFingerprint = AppSession.Current.Identity?.Fingerprint;
        return Service is not null && group.Members.All(member => member.Fingerprint == localFingerprint || Service.IsPeerConnected(member.Fingerprint));
    }

    private async void OnLeaveClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } group || Service is null) return;
        bool owner = group.OwnerFingerprint == AppSession.Current.Identity?.Fingerprint;
        if (owner && !AllRemoteMembersConnected(group))
        {
            SetStatus("所有现有群成员在线后才能移交群主。", InfoBarSeverity.Warning);
            RefreshSelection();
            return;
        }
        if (owner)
        {
            bool dissolves = group.Members.Count == 1;
            var dialog = new ContentDialog
            {
                Title = dissolves ? "结束群聊？" : "退出并转让群主？",
                Content = dissolves
                    ? "群聊将从本机移除，历史消息保留在本地存档中。"
                    : "服务将随机选择新的群主；此操作需要成员确认，未完成时群聊仍保留待处理状态。",
                PrimaryButtonText = "继续", CloseButtonText = "取消"
            };
            if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            try
            {
                await Service.LeaveAsOwnerAsync(group.Id);
                bool dissolved = Service.Store.GetGroup(group.Id) is null;
                SetStatus(dissolved ? "群聊已结束并从本机移除。" : "群主已退出，新的群主已随机选定。", InfoBarSeverity.Success);
            }
            catch (Exception ex) { SetStatus("群主离开事务未确认完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
            Refresh();
            return;
        }

        if (!group.Members.Any(member => member.Fingerprint == AppSession.Current.Identity?.Fingerprint) ||
            !_leaveRequestsInFlight.Add(group.Id)) return;
        RefreshSelection();
        try
        {
            await Service.RequestLeaveAsync(group.Id);
            bool stillMember = Service.Store.GetGroup(group.Id)?.Members.Any(member =>
                member.Fingerprint == AppSession.Current.Identity?.Fingerprint) == true;
            SetStatus(stillMember ? "退出申请已批准，正在等待成员名单同步。" : "群主已批准退出申请，已退出群组。",
                InfoBarSeverity.Success);
        }
        catch (Exception ex) { SetStatus("退出申请未完成：" + ErrorText.Describe(ex), InfoBarSeverity.Warning); }
        finally
        {
            _leaveRequestsInFlight.Remove(group.Id);
            Refresh();
        }
    }

    private async void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || Service is null || string.IsNullOrWhiteSpace(Composer.Text)) return;
        string text = Composer.Text;
        try
        {
            var results = await Service.SendTextAsync(_selected.Id, text);
            Composer.Text = "";
            SetStatus("各成员发送结果：" + string.Join("；", results.Select(FormatResult)), results.All(r => r.Status is V4GroupDeliveryStatus.Delivered or V4GroupDeliveryStatus.Local) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            RefreshSelection();
        }
        catch (Exception ex) { SetStatus("群消息发送未完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
    }

    private static string FormatResult(V4GroupMemberDeliveryResult r) => $"{r.MemberFingerprint}: {r.Status}{(string.IsNullOrWhiteSpace(r.Error) ? "" : "（" + r.Error + "）")}";

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || Service is null) return;
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        try
        {
            var results = await Service.SendFileAsync(_selected.Id, file.Path);
            SetStatus("文件发送结果：" + string.Join("；", results.Select(FormatResult)), results.All(r => r.Status is V4GroupDeliveryStatus.Delivered or V4GroupDeliveryStatus.Local) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { SetStatus("群文件发送未完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
    }

    private void SetStatus(string message, InfoBarSeverity severity) { StatusBar.Message = message; StatusBar.Severity = severity; StatusBar.IsOpen = true; }
}
