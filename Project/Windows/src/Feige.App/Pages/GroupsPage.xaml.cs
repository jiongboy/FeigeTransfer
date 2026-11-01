using System.Collections.Specialized;
using Feige.App.Services;
using Feige.Core.Conversations;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    private readonly HashSet<Guid> _leaveRequestsInFlight = [];

    public GroupsPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        GroupList.ItemsSource = AppSession.Current.Groups?.Groups;
        GroupList.ItemTemplate = GroupTemplate();
        MessageList.ItemsSource = null;
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
        bool showDetailPane = hasSelection || hasApprovals;
        EmptyGroups.Visibility = showDetailPane ? Visibility.Collapsed : Visibility.Visible;
        DetailPane.Visibility = showDetailPane ? Visibility.Visible : Visibility.Collapsed;
        if (hasSelection) return;

        GroupTitle.Text = hasApprovals ? "待处理请求" : "选择群组";
        MemberPanel.Children.Clear();
        MessageList.ItemsSource = null;
        EditMembersButton.IsEnabled = false;
        LeaveButton.IsEnabled = false;
        PickFileButton.IsEnabled = false;
        Composer.IsEnabled = false;
        SendButton.IsEnabled = false;
    }

    private static bool IsEligibleConversation(ConversationItem conversation)
        => !string.IsNullOrWhiteSpace(conversation.Fingerprint) &&
           AppSession.Current.Groups?.IsPeerConnected(conversation.Fingerprint) == true;

    private void OnGroupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _suppressSelectionChanged) return;
        GroupConversation? selected = GroupList.SelectedItem as GroupConversation;
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
        _selected = selected;
        _selectedGroupId = _selected?.Id;
        RefreshDetailPaneVisibility();
        if (_selected is not null) RefreshSelection();
    }

    private void RefreshSelection()
    {
        if (_selected is null || Service is null) return;
        var group = _selected;
        GroupTitle.Text = $"{group.Name} · {group.Members.Count} 人";
        MemberPanel.Children.Clear();
        MemberPanel.Children.Add(new TextBlock { Text = "成员", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        foreach (var member in group.Members)
        {
            bool online = member.Fingerprint == AppSession.Current.Identity?.Fingerprint || Service.IsPeerConnected(member.Fingerprint);
            MemberPanel.Children.Add(new TextBlock
            {
                Text = $"{(member.Fingerprint == group.OwnerFingerprint ? "群主 · " : "")}{member.DisplayName ?? member.Fingerprint} · {(online ? "已连接" : "未连接")}",
                TextWrapping = TextWrapping.Wrap
            });
        }
        bool localMember = group.Members.Any(member => member.Fingerprint == AppSession.Current.Identity?.Fingerprint);
        bool hasRemoteMember = group.Members.Any(member => member.Fingerprint != AppSession.Current.Identity?.Fingerprint);
        bool owner = group.OwnerFingerprint == AppSession.Current.Identity?.Fingerprint;
        bool hasPendingTransaction = Service.Operations.Any(op => op.GroupID == group.Id && op.Pending) ||
            Service.Store.PendingMembershipPrepares.Any(prepare => prepare.GroupID == group.Id);
        EditMembersButton.IsEnabled = owner && group.Members.Count > 1 && group.Members.All(m => m.Fingerprint == AppSession.Current.Identity?.Fingerprint || Service.IsPeerConnected(m.Fingerprint)) && !hasPendingTransaction;
        LeaveButton.Content = owner ? (group.Members.Count == 1 ? "解散群组" : "离开并移交群主") : "申请退出群组";
        LeaveButton.IsEnabled = localMember && !hasPendingTransaction &&
            (owner || (Service.IsPeerConnected(group.OwnerFingerprint) && !_leaveRequestsInFlight.Contains(group.Id)));
        PickFileButton.IsEnabled = localMember && group.Members.Any(member => member.Fingerprint != AppSession.Current.Identity?.Fingerprint && Service.IsPeerConnected(member.Fingerprint));
        Composer.IsEnabled = localMember && hasRemoteMember;
        SendButton.IsEnabled = localMember && hasRemoteMember;
        MessageList.ItemsSource = Service.Messages.Where(m => m.GroupID == group.Id).OrderBy(m => m.Date).Select(BuildMessageRow).ToArray();
    }

    private StackPanel BuildMessageRow(GroupMessageEnvelope message)
    {
        var row = new StackPanel { Spacing = 3, Margin = new Thickness(4, 8, 4, 8) };
        string sender = _selected?.Members.FirstOrDefault(m => m.Fingerprint == message.SenderFingerprint)?.DisplayName ?? message.SenderFingerprint;
        row.Children.Add(new TextBlock { Text = $"{sender} · {message.Date.ToLocalTime():yyyy-MM-dd HH:mm}", FontSize = 11 });
        row.Children.Add(new TextBlock { Text = message.Kind == GroupMessageKind.Text ? message.Text ?? "" : $"{(message.Kind == GroupMessageKind.Image ? "图片" : "文件")}: {message.FileMetadata?.FileName}", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
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
                    ? $"{approval.PeerName} 申请退出群组“{Service.Store.GetGroup(leave.GroupID)?.Name ?? "群组"}”"
                    : $"{approval.PeerName} 请求发送文件“{approval.File?.Metadata.FileName}”";
            card.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
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
            box.Children.Add(new TextBlock { Text = op.Pending ? $"成员事务处理中{(op.Stage is null ? "" : " · " + op.Stage)}" : "事务存在错误" , VerticalAlignment = VerticalAlignment.Center });
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
        var xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Padding='8,6'><TextBlock Text='{Binding Name}' FontWeight='SemiBold'/><TextBlock Text='{Binding Fingerprint}' FontSize='10' TextWrapping='Wrap'/></StackPanel></DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private async void OnEditMembersClick(object sender, RoutedEventArgs e)
    {
        if (_selected is null || Service is null) return;
        var group = _selected;
        var currentFingerprints = group.Members
            .Select(member => ConversationHistory.NormalizeFingerprint(member.Fingerprint))
            .ToHashSet(StringComparer.Ordinal);
        var connected = AppSession.Current.Conversations.Where(IsEligibleConversation).ToArray();
        var connectedFingerprints = connected
            .Select(conversation => ConversationHistory.NormalizeFingerprint(conversation.Fingerprint))
            .ToHashSet(StringComparer.Ordinal);
        var additions = connected.Where(conversation =>
            !currentFingerprints.Contains(ConversationHistory.NormalizeFingerprint(conversation.Fingerprint)))
            .Select(conversation => new GroupMember(conversation.Fingerprint, conversation.Name)).ToArray();
        var removable = group.Members.Where(member => member.Fingerprint != group.OwnerFingerprint &&
            connectedFingerprints.Contains(ConversationHistory.NormalizeFingerprint(member.Fingerprint))).ToArray();
        var addList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = additions, MaxHeight = 220, ItemTemplate = MemberTemplate() };
        var removeList = new ListView { SelectionMode = ListViewSelectionMode.Multiple, ItemsSource = removable, MaxHeight = 220, ItemTemplate = MemberTemplate() };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "成员变更要求当前群成员与新增成员均保持 v4 连接；新成员须先接受邀请。群组上限为 20 人，包含本机。" });
        content.Children.Add(new TextBlock { Text = "添加成员" }); content.Children.Add(addList);
        content.Children.Add(new TextBlock { Text = "移除成员" }); content.Children.Add(removeList);
        var dialog = new ContentDialog { Title = "变更群组成员", Content = content, PrimaryButtonText = "应用变更", CloseButtonText = "取消" };
        if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try
        {
            var add = addList.SelectedItems.OfType<GroupMember>().ToArray();
            var remove = removeList.SelectedItems.OfType<GroupMember>().Select(member => member.Fingerprint).ToHashSet(StringComparer.Ordinal);
            if (add.Length == 0 && remove.Count == 0) return;
            if (group.Members.Count - remove.Count + add.Length > GroupConversation.MaximumMembers)
                throw new InvalidOperationException("群组最多 20 人，包含本机；请减少所选成员。");
            var proposed = group.Members.Where(member => !remove.Contains(member.Fingerprint)).Concat(add).ToArray();
            await Service.ChangeMembersAsync(group.Id, proposed);
            SetStatus("成员变更已确认。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { SetStatus("成员变更未确认完成：" + ErrorText.Describe(ex), InfoBarSeverity.Error); }
        Refresh();
    }

    private DataTemplate MemberTemplate()
    {
        var xaml = "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Padding='8,6'><TextBlock Text='{Binding DisplayName}' FontWeight='SemiBold'/><TextBlock Text='{Binding Fingerprint}' FontSize='10' TextWrapping='Wrap'/></StackPanel></DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
    }

    private async void OnLeaveClick(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } group || Service is null) return;
        bool owner = group.OwnerFingerprint == AppSession.Current.Identity?.Fingerprint;
        if (owner)
        {
            bool dissolves = group.Members.Count == 1;
            var dialog = new ContentDialog
            {
                Title = dissolves ? "解散群组？" : "离开并移交群主？",
                Content = dissolves
                    ? "群组将从本机移除，历史消息保留在本地存档中。"
                    : "服务将随机选择新的群主；此操作需要成员确认，未完成时群组仍保留待处理状态。",
                PrimaryButtonText = "继续", CloseButtonText = "取消"
            };
            if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            try
            {
                await Service.LeaveAsOwnerAsync(group.Id);
                bool dissolved = Service.Store.GetGroup(group.Id) is null;
                SetStatus(dissolved ? "群组已结束并从本机移除。" : "群主已退出，新的群主已随机选定。", InfoBarSeverity.Success);
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
