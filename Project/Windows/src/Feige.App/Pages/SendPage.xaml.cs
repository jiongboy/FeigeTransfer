using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Feige.App.Services;
using Feige.Core.Conversations;
using Feige.Core.Discovery;
using Feige.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;

namespace Feige.App.Pages;

public sealed partial class SendPage : Page
{
    private ConversationItem? _selected;
    private bool _composing, _switching;
    private bool _showConversation;
    private bool _syncingConversations;
    private readonly ObservableCollection<ConversationItem> _conversationRows = [];
    private readonly HashSet<ConversationItem> _observedConversations = [];
    private string? _pageError;
    private bool _showNetworkAdvice;
    private static readonly (string Title, string[] Emojis)[] EmojiGroups =
    [
        ("常用", ["😀", "😃", "😄", "😁", "😆", "😅", "😂", "🙂", "🙃", "😉", "😊", "😍", "🥰", "😘", "😋", "😎", "🤔", "😴", "😭", "😡"]),
        ("手势", ["👋", "👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "👏", "🙌", "🫶", "💪", "🙏", "🤝"]),
        ("爱心", ["❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "💔", "💕", "💖", "💗", "💞", "💘"]),
        ("庆祝", ["🎉", "🎊", "🎂", "🎁", "🥳", "🏆", "✨", "🌟", "💯", "🔥", "👏", "🍾"]),
        ("动物", ["🐱", "🐶", "🐼", "🐸", "🐵", "🦊", "🐻", "🐰", "🐯", "🐨", "🦄", "🐣", "🐧", "🦋"]),
        ("日常", ["☀️", "🌙", "⭐", "⚡", "☕", "🍵", "🍰", "🍕", "🍉", "🌹", "🌻", "🌈", "🚀", "🎈"])
    ];
    private readonly Flyout _emojiFlyout;
    private int _emojiSelectionStart, _emojiSelectionLength;
    private AppSession Session => AppSession.Current;
    public void CaptureDraft() { if (_selected is not null) _selected.Draft = Composer.Text; }
    public SendPage()
    {
        InitializeComponent(); NavigationCacheMode = NavigationCacheMode.Required;
        _emojiFlyout = CreateEmojiFlyout();
        Composer.TextCompositionStarted += (_, _) => _composing = true;
        Composer.TextCompositionEnded += (_, _) => _composing = false;
    }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ConversationList.ItemsSource = _conversationRows; PeerList.ItemsSource = Session.Peers;
        SyncConversationRows();
        if (_selected is not null)
        { _selected.Messages.CollectionChanged -= OnMessagesChanged; _selected.Messages.CollectionChanged += OnMessagesChanged; }
        Session.Conversations.CollectionChanged += OnListsChanged;
        Session.Peers.CollectionChanged += OnListsChanged;
        Session.StateChanged += OnStateChanged; Session.StatusChanged += OnStatusChanged; Session.Failed += OnFailed;
        RefreshState();
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Session.StateChanged -= OnStateChanged; Session.StatusChanged -= OnStatusChanged; Session.Failed -= OnFailed;
        Session.Conversations.CollectionChanged -= OnListsChanged;
        Session.Peers.CollectionChanged -= OnListsChanged;
        foreach (var conversation in _observedConversations) conversation.PropertyChanged -= OnConversationChanged;
        _observedConversations.Clear();
        if (_selected is not null) _selected.Messages.CollectionChanged -= OnMessagesChanged;
        base.OnNavigatedFrom(e);
    }
    private void OnStateChanged(object? sender, EventArgs e) { SyncConversationRows(); RefreshState(); }
    private void OnStatusChanged(object? sender, string status) { _pageError = null; _showNetworkAdvice = false; RefreshState(); }
    private void OnFailed(object? sender, string error) => ShowError(error);
    private void RefreshState()
    {
        StatusBar.Message = _pageError ?? Session.DiscoveryIssue ?? Session.Status;
        if (_showNetworkAdvice) StatusBar.Message += "\n" + NetworkFailureAdvice.Message;
        StatusBar.Severity = _pageError is not null ? InfoBarSeverity.Error : Session.DiscoveryIssue is not null ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        StatusBar.IsOpen = _pageError is not null || Session.DiscoveryIssue is not null || !Session.IsRunning || Session.IsConnecting || Session.IsSendingFiles || Session.IsSendingText;
        StartupRetry.Visibility = _showNetworkAdvice || !Session.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        StartupRetry.Content = _showNetworkAdvice ? "连接排查" : "继续设置";
        StartupRetry.IsEnabled = _showNetworkAdvice || !Session.IsStarting;
        ManualButton.IsEnabled = PeerList.IsEnabled = Session.CanConnect;
        RefreshPeersButton.IsEnabled = Session.IsRunning && !Session.IsStarting && !Session.IsRefreshingPeers;
        RefreshPeersProgress.IsActive = Session.IsRefreshingPeers;
        RefreshPeersProgress.Visibility = Session.IsRefreshingPeers ? Visibility.Visible : Visibility.Collapsed;
        RefreshPeersIcon.Visibility = Session.IsRefreshingPeers ? Visibility.Collapsed : Visibility.Visible;
        string refreshLabel = Session.IsRefreshingPeers ? "正在刷新附近设备" : "刷新附近设备";
        ToolTipService.SetToolTip(RefreshPeersButton, refreshLabel);
        AutomationProperties.SetName(RefreshPeersButton, refreshLabel);
        DisconnectButton.IsEnabled = Session.IsConnecting || Session.ActiveFingerprint is not null;
        if (_selected is null && !Session.IsStarting)
            ConversationList.SelectedItem = _conversationRows.FirstOrDefault(c => c.Fingerprint == Session.ActiveFingerprint) ?? _conversationRows.FirstOrDefault();
        PeerTitle.Text = _selected?.Name ?? "消息";
        PeerAvatar.Text = _selected?.AvatarText ?? "鸽";
        ConnectionState.Text = _selected is null ? "选择设备，开始安全传输" : Session.CanSend(_selected) ? "已连接 · 可以发送消息和文件" : "未连接 · 会话记录保存在本机";
        DisconnectButton.Content = Session.IsConnecting ? "取消请求" : "断开连接";
        bool canSend = Session.CanSend(_selected);
        Composer.IsEnabled = canSend;
        Composer.PlaceholderText = canSend ? $"发送给 {_selected!.Name}" : "连接设备后，在这里输入消息…";
        SendButton.IsEnabled = canSend && !Session.IsSendingText && !string.IsNullOrWhiteSpace(Composer.Text);
        EmojiButton.IsEnabled = canSend;
        ImagesButton.IsEnabled = FilesButton.IsEnabled = canSend && !Session.IsSendingFiles;
        UpdateEmptyStates();
        UpdateLayoutForSize();
    }
    private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingConversations) SelectConversation(ConversationList.SelectedItem as ConversationItem);
    }
    private void SelectConversation(ConversationItem? conversation)
    {
        if (_selected is not null)
        { _selected.Draft = Composer.Text; _selected.Messages.CollectionChanged -= OnMessagesChanged; }
        _selected = conversation;
        _showConversation = _selected is not null;
        _switching = true; Composer.Text = _selected?.Draft ?? ""; _switching = false;
        MessageList.ItemsSource = _selected?.Messages;
        if (_selected is not null) _selected.Messages.CollectionChanged += OnMessagesChanged;
        RefreshState(); ScrollToEnd();
    }
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { UpdateEmptyStates(); ScrollToEnd(); }
    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    { SyncConversationRows(); RefreshState(); }
    private void OnConversationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConversationItem.LastActivityText)) SyncConversationRows();
        if (e.PropertyName == nameof(ConversationItem.Name) && ReferenceEquals(sender, _selected)) RefreshState();
    }
    private void SyncConversationRows()
    {
        if (_syncingConversations) return;
        _syncingConversations = true;
        try
        {
            foreach (var removed in _observedConversations.Where(c => !Session.Conversations.Contains(c)).ToArray())
            {
                removed.PropertyChanged -= OnConversationChanged;
                _observedConversations.Remove(removed);
            }
            foreach (var removed in _conversationRows.Where(c => !Session.Conversations.Contains(c) || Session.IsConversationHidden(c)).ToArray())
                _conversationRows.Remove(removed);
            foreach (var conversation in Session.Conversations)
                if (_observedConversations.Add(conversation)) conversation.PropertyChanged += OnConversationChanged;
            // Move existing objects so a new message never discards the selected conversation or its draft.
            var ordered = Session.Conversations.Where(c => !Session.IsConversationHidden(c)).OrderByDescending(c => c.Messages.LastOrDefault()?.Entry.At ?? DateTimeOffset.MinValue).ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                int previous = _conversationRows.IndexOf(ordered[index]);
                if (previous < 0) _conversationRows.Insert(index, ordered[index]);
                else if (previous != index) _conversationRows.Move(previous, index);
            }
            ConversationList.SelectedItem = _selected is not null && _conversationRows.Contains(_selected) ? _selected : null;
        }
        finally { _syncingConversations = false; }
        if (_selected is not null && !_conversationRows.Contains(_selected)) SelectConversation(null);
    }
    private void UpdateEmptyStates()
    {
        ConversationCount.Text = $"会话 · {_conversationRows.Count}";
        PeerCount.Text = $"附近设备 · {Session.Peers.Count}";
        PeerRefreshStatus.Visibility = Session.IsRefreshingPeers || Session.PeerRefreshFailed || Session.LastPeerRefreshAt is not null
            ? Visibility.Visible : Visibility.Collapsed;
        PeerRefreshStatus.Text = Session.IsRefreshingPeers ? "正在查找设备…"
            : Session.PeerRefreshFailed ? "刷新未完成，请重试或查看连接帮助。"
            : Session.LastPeerRefreshAt is { } refreshed ? $"{(Session.Peers.Count == 0 ? "暂未发现设备" : "已刷新")} · {refreshed:HH:mm:ss}" : "";
        PeersEmpty.Text = Session.IsRefreshingPeers ? "正在查找同一局域网中的设备，请稍候…"
            : "请在另一台电脑打开飞鸽快传，并连接到同一局域网。可刷新设备、用 + 手动连接，或查看连接帮助。";
        ConversationsEmpty.Visibility = _conversationRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasHidden = Session.Conversations.Any(Session.IsConversationHidden);
        EmptyConversationsTitle.Text = hasHidden ? "暂无可见会话" : "还没有会话";
        EmptyConversationsDescription.Text = hasHidden ? "可在设置中恢复隐藏会话，收到新消息时也会再次显示。" : "连接附近设备后，会话将显示在这里。";
        PeersEmpty.Visibility = Session.Peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChatEmpty.Visibility = _selected?.Messages.Count is null or 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _selected is null ? "让传输像聊天一样简单" : "开启一段新会话";
        EmptyDescription.Text = _selected is null ? "从附近设备发起连接，轻松互传文字、图片和文件。" :
            Session.CanSend(_selected) ? "连接成功，发送第一条消息或拖入文件吧。" : "先连接此设备，即可继续聊天和传输文件。";
    }
    private void OnConversationContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        ConversationItem? conversation = null;
        for (DependencyObject? node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, ConversationList); node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem container && ConversationList.ItemFromContainer(container) is ConversationItem item)
            { conversation = item; break; }
        }
        bool positioned = args.TryGetPosition(ConversationList, out var position);
        if (conversation is null && !positioned) conversation = ConversationList.SelectedItem as ConversationItem;
        if (conversation is null || !Session.Conversations.Contains(conversation) || !_conversationRows.Contains(conversation)) return;
        args.Handled = true;
        var target = conversation;
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "打开会话" };
        AutomationProperties.SetAutomationId(open, "OpenConversationMenuItem");
        open.Click += (_, _) => { ConversationList.SelectedItem = target; _showConversation = true; UpdateLayoutForSize(); };
        menu.Items.Add(open);
        if (Session.ActiveFingerprint == target.Fingerprint)
        {
            var disconnect = new MenuFlyoutItem { Text = "断开连接" };
            AutomationProperties.SetAutomationId(disconnect, "DisconnectConversationMenuItem");
            disconnect.Click += async (_, _) => { try { await Session.DisconnectAsync(); } catch (Exception ex) { ShowError(ErrorText.Describe(ex)); } };
            menu.Items.Add(disconnect);
        }
        var hide = new MenuFlyoutItem { Text = "隐藏会话" };
        AutomationProperties.SetAutomationId(hide, "HideConversationMenuItem");
        hide.Click += async (_, _) => { try { await Session.SetConversationHiddenAsync(target, true); } catch (Exception ex) { ShowError("隐藏失败：" + ErrorText.Describe(ex)); } };
        menu.Items.Add(hide);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = new MenuFlyoutItem { Text = "删除会话记录…", IsEnabled = Session.CanDeleteConversation(target) };
        AutomationProperties.SetAutomationId(delete, "DeleteConversationMenuItem");
        ToolTipService.SetToolTip(delete, delete.IsEnabled ? "删除本机会话记录，保留已收发的文件" : "请先断开此会话的连接，并等待传输结束");
        delete.Click += async (_, _) => await DeleteConversationAsync(target);
        menu.Items.Add(delete);
        if (positioned) menu.ShowAt(ConversationList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position });
        else menu.ShowAt(ConversationList.ContainerFromItem(target) as FrameworkElement ?? ConversationList);
    }

    private async Task DeleteConversationAsync(ConversationItem conversation)
    {
        var dialog = new ContentDialog
        {
            Title = $"删除与“{conversation.Name}”的会话记录？",
            Content = "将删除本机保存的这段聊天记录，删除后无法恢复。已经发送或接收的文件仍保留在磁盘上。",
            PrimaryButtonText = "删除记录", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        try
        {
            if (await UiPromptService.Current.ShowAsync(dialog) == ContentDialogResult.Primary)
                await Session.DeleteConversationAsync(conversation);
        }
        catch (Exception ex) { ShowError("删除失败：" + ErrorText.Describe(ex)); }
    }

    private void OnConversationClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ConversationItem conversation && !ReferenceEquals(_selected, conversation))
            ConversationList.SelectedItem = conversation;
        _showConversation = true; UpdateLayoutForSize();
    }
    private void OnConversationsBackClick(object sender, RoutedEventArgs e)
    { _showConversation = false; UpdateLayoutForSize(); }
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutForSize();
    private void UpdateLayoutForSize()
    {
        if (ConversationPane is null || ChatPane is null || ConversationsBackButton is null || ComposerHint is null) return;
        bool compact = PageRoot.ActualWidth < 700;
        bool showChat = !compact || (_showConversation && _selected is not null);
        ConversationColumn.Width = compact ? new GridLength(showChat ? 0 : 1, showChat ? GridUnitType.Pixel : GridUnitType.Star)
            : new GridLength(PageRoot.ActualWidth < 860 ? 260 : 292);
        ChatColumn.Width = new GridLength(showChat ? 1 : 0, showChat ? GridUnitType.Star : GridUnitType.Pixel);
        ConversationPane.Visibility = compact && showChat ? Visibility.Collapsed : Visibility.Visible;
        ChatPane.Visibility = showChat ? Visibility.Visible : Visibility.Collapsed;
        ConversationsBackButton.Visibility = compact && showChat ? Visibility.Visible : Visibility.Collapsed;
        ComposerHint.Visibility = PageRoot.ActualHeight < 440 ? Visibility.Collapsed : Visibility.Visible;
        Composer.MinHeight = PageRoot.ActualHeight < 440 ? 40 : 58;
    }
    private void ScrollToEnd() => DispatcherQueue.TryEnqueue(() =>
    { if (_selected?.Messages.LastOrDefault() is { } last) MessageList.ScrollIntoView(last); });
    private Flyout CreateEmojiFlyout()
    {
        var content = new StackPanel { Spacing = 10, Padding = new Thickness(12) };
        foreach (var (title, emojis) in EmojiGroups)
        {
            var group = new StackPanel { Spacing = 4 };
            group.Children.Add(new TextBlock { Text = title, FontSize = 12 });
            var grid = new WrapGrid
            {
                Orientation = Orientation.Horizontal,
                MaximumRowsOrColumns = 6,
                ItemWidth = 38,
                ItemHeight = 38
            };
            foreach (string emoji in emojis)
            {
                var button = new Button
                {
                    Content = emoji,
                    Tag = emoji,
                    Width = 38,
                    Height = 38,
                    Padding = new Thickness(0),
                    FontSize = 20
                };
                AutomationProperties.SetName(button, $"插入表情 {emoji}");
                ToolTipService.SetToolTip(button, emoji);
                button.Click += OnEmojiSelected;
                grid.Children.Add(button);
            }
            group.Children.Add(grid);
            content.Children.Add(group);
        }

        return new Flyout
        {
            Content = new ScrollViewer
            {
                Content = content,
                Width = 282,
                MaxHeight = 360,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        };
    }

    private void OnEmojiPickerClick(object sender, RoutedEventArgs e)
    {
        if (!Composer.IsEnabled) return;
        _emojiSelectionStart = Composer.SelectionStart;
        _emojiSelectionLength = Composer.SelectionLength;
        _emojiFlyout.ShowAt(EmojiButton);
    }

    private void OnEmojiSelected(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string emoji }) return;
        string text = Composer.Text;
        int start = Math.Clamp(_emojiSelectionStart, 0, text.Length);
        int length = Math.Clamp(_emojiSelectionLength, 0, text.Length - start);
        Composer.Text = text.Remove(start, length).Insert(start, emoji);
        Composer.SelectionStart = start + emoji.Length;
        Composer.SelectionLength = 0;
        _emojiFlyout.Hide();
        Composer.Focus(FocusState.Programmatic);
    }

    private void OnDraftChanged(object sender, TextChangedEventArgs e)
    {
        if (_switching) return;
        if (_selected is not null) _selected.Draft = Composer.Text;
        if (SendButton is not null) RefreshState();
    }
    private async void OnComposerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool controlDown = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        bool pasteShortcut = (controlDown && e.Key == VirtualKey.V) ||
            (e.Key == VirtualKey.Insert && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0);
        if (pasteShortcut && Clipboard.GetContent().Contains(StandardDataFormats.StorageItems))
        {
            e.Handled = true;
            await PasteFilesFromClipboardAsync();
            return;
        }
        if (e.Key != VirtualKey.Enter || _composing || !SendButton.IsEnabled) return;
        if ((Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0) return;
        e.Handled = true; await SendTextAsync();
    }
    private async Task PasteFilesFromClipboardAsync()
    {
        var conversation = _selected;
        if (!Session.CanSend(conversation) || Session.IsSendingFiles) return;
        try
        {
            var items = await Clipboard.GetContent().GetStorageItemsAsync();
            if (items.Count == 0) return;
            if (items.Any(item => item is not StorageFile))
                throw new ArgumentException("目前仅支持文件，请先将文件夹打包。");
            await Session.SendFilesAsync(conversation!, items.Select(item => item.Path).ToArray());
        }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
    }
    private async void OnSendTextClick(object sender, RoutedEventArgs e) => await SendTextAsync();
    private async Task SendTextAsync()
    {
        var conversation = _selected; if (conversation is null) return;
        string text = Composer.Text;
        try
        {
            await Session.SendTextAsync(conversation, text);
            if (conversation.Draft == text) conversation.Draft = "";
            if (ReferenceEquals(_selected, conversation) && Composer.Text == text) Composer.Text = "";
        }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
        RefreshState();
    }
    private async void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DiscoveredPeer peer }) return;
        try { await Session.ConnectAsync(peer); SelectActive(); }
        catch (Exception ex) { ShowConnectionError(ex); }
    }
    private void SelectActive() => ConversationList.SelectedItem = Session.Conversations.FirstOrDefault(c => c.Fingerprint == Session.ActiveFingerprint);
    private async void OnManualClick(object sender, RoutedEventArgs e)
    {
        var address = new TextBox { Header = "局域网 IP", PlaceholderText = "192.168.1.20 或 fd00::1" };
        var port = new TextBox { Header = "端口", PlaceholderText = "对方连接信息中显示的端口" };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(address, "ManualAddressBox");
        AutomationProperties.SetAutomationId(port, "ManualPortBox");
        AutomationProperties.SetAutomationId(error, "ManualConnectionError");
        var form = new StackPanel { Spacing = 12 }; form.Children.Add(address); form.Children.Add(port); form.Children.Add(error);
        var dialog = new ContentDialog { Title = "手动连接", Content = form, PrimaryButtonText = "请求连接", CloseButtonText = "取消" };
        AutomationProperties.SetAutomationId(dialog, "ManualConnectionDialog");
        System.Net.IPEndPoint? endpoint = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                if (!int.TryParse(port.Text.Trim(), out int value)) throw new ArgumentException("端口应为 1–65535 的数字。");
                endpoint = V3PeerConnector.ParseTarget(address.Text.Trim(), value);
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        try
        {
            if (await UiPromptService.Current.ShowAsync(dialog) != ContentDialogResult.Primary || endpoint is null) return;
            await Session.ConnectAsync(endpoint); SelectActive();
        }
        catch (Exception ex) { ShowConnectionError(ex); }
    }
    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (Session.IsRefreshingPeers) return;
        _pageError = null; _showNetworkAdvice = false;
        try { await Session.RefreshPeersAsync(); }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
    }
    private async void OnConnectionHelpClick(object sender, RoutedEventArgs e) => await ConnectionHelp.ShowAsync();
    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_showNetworkAdvice) await ConnectionHelp.ShowNetworkTroubleshootingAsync();
        else ((MainWindow)App.MainWindow!).ContinueSetup();
    }
    private async void OnDisconnectClick(object sender, RoutedEventArgs e)
    { try { await Session.DisconnectAsync(); } catch (Exception ex) { ShowError(ErrorText.Describe(ex)); } }
    private async void OnImagesClick(object sender, RoutedEventArgs e) => await PickAttachmentsAsync(true);
    private async void OnFilesClick(object sender, RoutedEventArgs e) => await PickAttachmentsAsync(false);
    private async Task PickAttachmentsAsync(bool images)
    {
        var conversation = _selected; if (!Session.CanSend(conversation)) return;
        try
        {
            var picker = new FileOpenPicker();
            foreach (string type in images ? new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".heic" } : new[] { "*" }) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow!));
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0) await Session.SendFilesAsync(conversation!, files.Select(f => f.Path).ToArray(), images);
        }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = Session.CanSend(_selected) && !Session.IsSendingFiles && e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        var conversation = _selected; if (!Session.CanSend(conversation)) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Any(i => i is not StorageFile)) throw new ArgumentException("目前仅支持文件，请先将文件夹打包。");
            await Session.SendFilesAsync(conversation!, items.Select(i => i.Path).ToArray());
        }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
    }
    private MessageItem? MessageFromElement(DependencyObject element)
    {
        for (DependencyObject? node = element; node is not null && !ReferenceEquals(node, MessageList); node = VisualTreeHelper.GetParent(node))
            if (node is ListViewItem container) return MessageList.ItemFromContainer(container) as MessageItem;
        return null;
    }

    private void OnAcceptIncomingFileClick(object sender, RoutedEventArgs e) => DecideIncomingFile(sender, accept: true);
    private void OnDeclineIncomingFileClick(object sender, RoutedEventArgs e) => DecideIncomingFile(sender, accept: false);
    private void DecideIncomingFile(object sender, bool accept)
    {
        if (sender is not DependencyObject element || MessageFromElement(element) is not { } item || _selected is null) return;
        if (!Session.DecideIncomingFile(_selected, item, accept))
            ShowError("此文件请求已超时或连接已断开。");
    }

    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || MessageFromElement(element) is not { } item || _selected is null) return;
        try
        {
            if (item.Entry.Kind == MessageKind.Text) await Session.SendTextAsync(_selected, item.Entry.Text, item);
            else await Session.SendFilesAsync(_selected, [item.Entry.Path], item.Entry.Kind == MessageKind.Image);
        }
        catch (Exception ex) { ShowError(ErrorText.Describe(ex)); }
    }
    private async void OnPreviewAttachmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || MessageFromElement(element) is not { } item || !item.CanOpenPreview) return;
        if (element is Control control) control.IsEnabled = false;
        try { await UiPromptService.Current.ShowAsync(new Feige.App.Controls.AttachmentPreviewDialog(item.Entry.Path, item.Entry.FileName, XamlRoot, item.Entry.Kind == MessageKind.Image)); }
        catch (Exception ex) { ShowError("无法打开预览。" + ErrorText.Describe(ex)); }
        finally { if (element is Control button) button.IsEnabled = true; }
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || MessageFromElement(element) is not { } item) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Entry.Path);
            if (!await Launcher.LaunchFileAsync(file)) throw new IOException("系统未能打开该文件，请检查是否有相应应用。");
        }
        catch (Exception ex) { ShowError("文件可能已移动或删除。" + ErrorText.Describe(ex)); }
    }
    private async void OnRevealFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || MessageFromElement(element) is not { } item) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Entry.Path);
            var options = new FolderLauncherOptions(); options.ItemsToSelect.Add(file);
            await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
        }
        catch (Exception ex) { ShowError("无法显示文件位置。" + ErrorText.Describe(ex)); }
    }
    private void OnImageDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is not Image image) return;
        image.Source = null;
        if (image.IsLoaded) OnImageLoaded(image, new RoutedEventArgs());
    }

    private async void OnImageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image || MessageFromElement(image) is not { } item || !item.CanPreview) return;
        try
        {
            var bitmap = await ImagePreview.LoadAsync(item.Entry.Path, 640);
            if (ReferenceEquals(MessageFromElement(image), item)) image.Source = bitmap;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(MessageFromElement(image), item)) image.Source = null;
            AppLog.Write("image preview: " + ex.GetType().Name);
        }
    }
    private void ShowConnectionError(Exception error)
        => ShowError(ErrorText.Describe(error), NetworkFailureAdvice.IsApplicable(error));
    private void ShowError(string message, bool networkAdvice = false)
    { _pageError = message; _showNetworkAdvice = networkAdvice; RefreshState(); }
}
