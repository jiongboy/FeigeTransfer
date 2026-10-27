using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Feige.Core.Conversations;
using Feige.Core.Storage;
using Feige.Core.Preview;
using Microsoft.UI.Xaml;

namespace Feige.App.Services;

public sealed class ConversationItem(string fingerprint, string name) : INotifyPropertyChanged
{
    private readonly ConversationDisplayName _displayName = new(fingerprint, name);
    public string Fingerprint => _displayName.Fingerprint;
    private string _state = "未连接", _summary = "暂无消息";
    public string Name { get => _displayName.Name; set { if (_displayName.SetName(value)) NameChanged(); } }
    public void ApplyDiscoveredName(Feige.Core.Discovery.DiscoveredPeer peer)
    {
        if (_displayName.ApplyDiscovery(peer)) NameChanged();
    }
    private void NameChanged() { Changed(nameof(Name)); Changed(nameof(AvatarText)); }
    public string State { get => _state; set { _state = value; Changed(nameof(State)); } }
    public string Summary { get => _summary; set { _summary = value; Changed(nameof(Summary)); Changed(nameof(LastActivityText)); } }
    public string AvatarText => string.IsNullOrWhiteSpace(Name) ? "鸽" : StringInfo.GetNextTextElement(Name.Trim()).ToUpperInvariant();
    public string LastActivityText => Messages.LastOrDefault() is not { } message ? "" :
        message.Entry.At.ToLocalTime().ToString(message.Entry.At.ToLocalTime().Date == DateTime.Today ? "HH:mm" : "MM/dd");
    public string Draft { get; set; } = "";
    public ObservableCollection<MessageItem> Messages { get; } = [];
    public override string ToString() => Name;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class MessageItem(ConversationMessage entry) : INotifyPropertyChanged
{
    public ConversationMessage Entry { get; private set; } = entry;
    private long _transferred;
    private double _speedBytesPerSecond;
    public Guid Id => Entry.Id;
    public string Body => Entry.Kind == MessageKind.Text ? Entry.Text : Entry.FileName;
    public string Caption => $"{(Entry.Direction == TransferDirection.Sent ? "我" : Entry.PeerName)} · {Entry.At.ToLocalTime():MM-dd HH:mm}";
    public HorizontalAlignment Alignment => Entry.Direction == TransferDirection.Sent ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Style BubbleStyle => (Style)Application.Current.Resources[
        Entry.Direction == TransferDirection.Sent ? "FeigeSentBubbleStyle" : "FeigeReceivedBubbleStyle"];
    public override string ToString() => Body;
    private bool IsCompleted => Entry.State is MessageState.Delivered or MessageState.Received;
    public Visibility TextVisibility => Entry.Kind == MessageKind.Text ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletionVisibility => IsCompleted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AttachmentActionsVisibility => CanOpen ? Visibility.Visible : Visibility.Collapsed;
    private PreviewFormat Format => Entry.Kind == MessageKind.Image ? new(PreviewKind.Image, "图片") : AttachmentPreview.GetFormat(Entry.FileName);
    public bool CanOpenPreview => CanOpen && Format.Kind != PreviewKind.Unsupported;
    public Visibility PreviewActionVisibility => CanOpenPreview ? Visibility.Visible : Visibility.Collapsed;
    public string FileGlyph => Format.Kind == PreviewKind.Image ? "\uEB9F" : "\uE8A5";
    public string OpenLabel => Format.Kind == PreviewKind.Image ? "打开图片" : "打开文件";
    public string AttachmentMetadata
    {
        get
        {
            string extension = Path.GetExtension(Entry.FileName).TrimStart('.').ToUpperInvariant();
            string kind = Format.Kind == PreviewKind.Image ? "图片" : "文件";
            return $"{(extension.Length is > 0 and <= 10 ? extension + " " : "")}{kind} · {SizeText}";
        }
    }
    public Visibility AttachmentVisibility => Entry.Kind == MessageKind.Text ? Visibility.Collapsed : Visibility.Visible;
    public bool CanPreview => Entry.Kind != MessageKind.Text && Format.Kind == PreviewKind.Image && IsCompleted;
    public Visibility ImageVisibility => Entry.Kind != MessageKind.Text && Format.Kind == PreviewKind.Image && IsCompleted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RetryVisibility => (Entry.State is MessageState.Failed or MessageState.Declined) && Entry.Direction == TransferDirection.Sent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Entry.Kind != MessageKind.Text && Entry.IsActive ? Visibility.Visible : Visibility.Collapsed;
    public bool CanOpen => Entry.Kind != MessageKind.Text && IsCompleted;
    public double Percent => Entry.Size <= 0 ? 0 : Math.Clamp(_transferred * 100.0 / Entry.Size, 0, 100);
    public string SizeText => UiPromptService.FormatSize(Entry.Size);
    public string RateText => _speedBytesPerSecond > 0 && Entry.IsActive
        ? UiPromptService.FormatSize((long)_speedBytesPerSecond) + "/秒"
        : "";
    public Visibility RateVisibility => Entry.Kind != MessageKind.Text && Entry.IsActive && _speedBytesPerSecond > 0
        ? Visibility.Visible : Visibility.Collapsed;
    public string Status => Entry.State switch
    {
        MessageState.Queued => "等待发送",
        MessageState.Sending when Entry.Kind != MessageKind.Text && Entry.Direction == TransferDirection.Sent && Entry.Error == "等待对方确认接收" => "等待对方确认接收…",
        MessageState.Sending when Entry.Kind != MessageKind.Text && Entry.Direction == TransferDirection.Received && Entry.Error == "等待确认接收" => "等待确认接收",
        MessageState.Sending when Entry.Kind != MessageKind.Text && Entry.Direction == TransferDirection.Received && Entry.Error == "接收已确认" => "已确认接收，等待传输…",
        MessageState.Sending => Entry.Kind == MessageKind.Text ? "正在发送…" : _transferred >= Entry.Size ? "等待校验与回执…" : $"传输中 · {UiPromptService.FormatSize(_transferred)} / {SizeText}",
        MessageState.Delivered => Entry.Kind == MessageKind.Text ? "已送达" : "已发送",
        MessageState.Received => "已接收",
        MessageState.Declined => Entry.Direction == TransferDirection.Sent ? "对方拒绝接收" : "已拒绝接收",
        _ => "未完成 · " + Entry.Error,
    };
    public Visibility IncomingDecisionVisibility => Entry.Direction == TransferDirection.Received && Entry.Kind != MessageKind.Text &&
        Entry.State == MessageState.Sending && Entry.Error == "等待确认接收" ? Visibility.Visible : Visibility.Collapsed;
    public void Apply(ConversationMessage entry)
    {
        Entry = entry;
        foreach (var name in new[] { nameof(Body), nameof(Caption), nameof(Status), nameof(RetryVisibility), nameof(ProgressVisibility), nameof(CanOpen), nameof(ImageVisibility), nameof(CanPreview), nameof(SizeText), nameof(AttachmentMetadata), nameof(CompletionVisibility), nameof(AttachmentActionsVisibility), nameof(TextVisibility), nameof(AttachmentVisibility), nameof(FileGlyph), nameof(OpenLabel), nameof(Percent), nameof(CanOpenPreview), nameof(PreviewActionVisibility), nameof(RateText), nameof(RateVisibility), nameof(IncomingDecisionVisibility) })
            PropertyChanged?.Invoke(this, new(name));
    }
    public void SetProgress(long bytes, double speedBytesPerSecond = 0)
    {
        _transferred = bytes;
        _speedBytesPerSecond = Math.Max(0, speedBytesPerSecond);
        PropertyChanged?.Invoke(this, new(nameof(Percent)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
        PropertyChanged?.Invoke(this, new(nameof(IncomingDecisionVisibility)));
        PropertyChanged?.Invoke(this, new(nameof(RateText)));
        PropertyChanged?.Invoke(this, new(nameof(RateVisibility)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
