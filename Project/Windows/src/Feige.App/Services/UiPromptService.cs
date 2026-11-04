using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Feige.App.Services;

public enum ConnectionConsent { Rejected, Accepted }

/// <summary>Serializes native dialogs and closes an outstanding connection prompt on cancellation.</summary>
public sealed class UiPromptService
{
    public static UiPromptService Current { get; } = new();
    private DispatcherQueue? _dispatcher;
    private Func<XamlRoot?>? _root;
    private readonly SemaphoreSlim _dialogs = new(1);
    public void Initialize(DispatcherQueue dispatcher, Func<XamlRoot?> rootProvider) { _dispatcher = dispatcher; _root = rootProvider; }

    /// <summary>Approval is asked once per device; the caller makes an accepted peer a persistent contact.</summary>
    public Task<ConnectionConsent> ConfirmConnectionAsync(string name, string folder, CancellationToken ct)
        => OnUiAsync(async () =>
        {
            App.ShowMainWindow();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = $"“{name}”请求与你连接。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "同意后可双向发送文字、图片和文件。只需同意一次：以后这台设备连接无需再确认，上线时自动连接；可在设置的常驻联系人中移除。接收文件仍需确认。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "接收位置：" + folder, TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog { Title = "连接请求", Content = content,
                PrimaryButtonText = "同意连接", CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Close };
            return await ShowAsync(dialog, ct) switch
            {
                ContentDialogResult.Primary => ConnectionConsent.Accepted,
                _ => ConnectionConsent.Rejected,
            };
        });

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken ct = default)
    {
        await _dialogs.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            dialog.XamlRoot = _root?.Invoke() ?? throw new InvalidOperationException("窗口尚未准备好。");
            using var registration = ct.Register(() => _dispatcher?.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync();
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally { _dialogs.Release(); }
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        if (_dispatcher is null) throw new InvalidOperationException("窗口尚未准备好。");
        if (_dispatcher.HasThreadAccess) return action();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () => { try { completion.TrySetResult(await action()); } catch (Exception ex) { completion.TrySetException(ex); } }))
            completion.TrySetException(new OperationCanceledException("窗口已退出。"));
        return completion.Task;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B", < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB", _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
