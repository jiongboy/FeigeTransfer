using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Feige.App.Services;

/// <summary>Serializes native dialogs and closes an outstanding connection prompt on cancellation.</summary>
public sealed class UiPromptService
{
    public static UiPromptService Current { get; } = new();
    private DispatcherQueue? _dispatcher;
    private Func<XamlRoot?>? _root;
    private readonly SemaphoreSlim _dialogs = new(1);
    public void Initialize(DispatcherQueue dispatcher, Func<XamlRoot?> rootProvider) { _dispatcher = dispatcher; _root = rootProvider; }

    public Task<bool> ConfirmConnectionAsync(string name, string folder, CancellationToken ct)
        => OnUiAsync(async () =>
        {
            App.ShowMainWindow();
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = $"“{name}”请求与你连接。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "同意后，本次连接内可双向发送文字、图片和文件。断开后再次连接需要重新同意。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "接收位置：" + folder, TextWrapping = TextWrapping.Wrap });
            return await ShowAsync(new ContentDialog { Title = "连接请求", Content = content,
                PrimaryButtonText = "同意连接", CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Close }, ct) == ContentDialogResult.Primary;
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
