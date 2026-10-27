using Feige.App.Services;
using Feige.Core.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.RegularExpressions;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Feige.App.Controls;

public sealed partial class AttachmentPreviewDialog : ContentDialog
{
    private readonly string _path;
    private readonly PreviewFormat _format;
    private readonly CancellationTokenSource _cancel = new();
    private PdfDocument? _pdf;
    private uint _page;
    private string _text = "";
    private bool _closed;
    private bool _rendering;
    private double _visualAspect = 1;

    public AttachmentPreviewDialog(string path, string name, XamlRoot root, bool isImage = false)
    {
        InitializeComponent();
        _path = path;
        _format = isImage ? new(PreviewKind.Image, "图片") : AttachmentPreview.GetFormat(name);
        FileTitle.Text = name;
        ToolTipService.SetToolTip(FileTitle, name);
        FormatText.Text = _format.Label;
        PreviewLayout.Width = Math.Max(200, Math.Min(920, root.Size.Width - 112));
        PreviewLayout.Height = Math.Max(140, Math.Min(600, root.Size.Height - 220));
        DocumentImage.Width = PreviewLayout.Width - 2;
        DocumentImage.Height = Math.Max(100, PreviewLayout.Height - 100);
        // On small windows the two tool groups use separate rows.
        if (PreviewLayout.Width < 500)
        {
            VisualToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            VisualToolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow((FrameworkElement)VisualToolbar.Children[1], 1);
            Grid.SetColumn((FrameworkElement)VisualToolbar.Children[1], 0);
            VisualToolbar.RowSpacing = 6;
        }
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        try
        {
            if (!File.Exists(_path)) throw new FileNotFoundException();
            switch (_format.Kind)
            {
                case PreviewKind.Text:
                case PreviewKind.Code:
                case PreviewKind.Word:
                    var result = await AttachmentPreview.ReadTextAsync(_path, _format.Kind, _cancel.Token);
                    if (_closed) return;
                    _text = result.Text;
                    CopyPreviewButton.IsEnabled = _text.Length > 0;
                    FormatText.Text = $"{_format.Label} · {UiPromptService.FormatSize(new FileInfo(_path).Length)} · {result.EncodingName}";
                    bool code = _format.Kind == PreviewKind.Code;
                    WrapPreviewButton.Visibility = CopyPreviewButton.Visibility = Visibility.Visible;
                    WrapPreviewButton.IsChecked = !code;
                    PreviewText.FontFamily = new FontFamily(code ? "Consolas" : "Segoe UI");
                    PreviewText.FontSize = code ? 13 : 14;
                    LineNumbers.Text = string.Join('\n', Enumerable.Range(1, Math.Max(1, _text.Count(ch => ch == '\n') + 1)));
                    RenderText(code);
                    UpdateWrap();
                    TextScroller.Visibility = Visibility.Visible;
                    if (result.Truncated) Notice("文件较长，当前显示前一部分内容；复制仅包含当前预览。");
                    else if (_format.Kind == PreviewKind.Word) Notice("仅预览正文文本；图片、图表和完整排版请使用“打开文件”查看。");
                    break;
                case PreviewKind.Image:
                    await LoadImageAsync();
                    break;
                case PreviewKind.Pdf:
                    if (new FileInfo(_path).Length > 128L * 1024 * 1024) throw new InvalidDataException("PDF 较大，请使用“打开文件”查看。");
                    _pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(_path));
                    if (_closed) return;
                    if (_pdf.PageCount == 0) throw new InvalidDataException("PDF 中没有可显示的页面。");
                    PdfNavigation.Visibility = Visibility.Visible;
                    await RenderPageAsync();
                    break;
                default:
                    throw new NotSupportedException("暂不支持此格式的应用内预览。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) ShowError(ex); }
        finally { if (!_closed) PreviewLoading.IsActive = false; }
    }

    private async Task LoadImageAsync()
    {
        var bitmap = await ImagePreview.LoadAsync(_path, 1600);
        if (_closed) return;
        SetVisual(bitmap);
    }

    private async Task RenderPageAsync()
    {
        if (_pdf is null || _rendering || _closed) return;
        _rendering = true;
        PreviousPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        PreviewLoading.IsActive = true;
        try
        {
            using var page = _pdf.GetPage(_page);
            using var stream = new InMemoryRandomAccessStream();
            double scale = Math.Min(1600 / Math.Max(1, page.Size.Width), 2200 / Math.Max(1, page.Size.Height));
            await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) });
            var bitmap = new BitmapImage();
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            if (_closed) return;
            _visualAspect = page.Size.Width / Math.Max(1, page.Size.Height);
            DocumentImage.Source = bitmap;
            PdfPageText.Text = $"{_page + 1} / {_pdf.PageCount}";
            ShowVisual();
            VisualScroller.ChangeView(0, 0, 1);
        }
        finally
        {
            _rendering = false;
            if (!_closed)
            {
                PreviewLoading.IsActive = false;
                PreviousPageButton.IsEnabled = _page > 0;
                NextPageButton.IsEnabled = _page + 1 < _pdf.PageCount;
            }
        }
    }

    private void SetVisual(BitmapImage bitmap)
    {
        _visualAspect = bitmap.PixelWidth / (double)Math.Max(1, bitmap.PixelHeight);
        DocumentImage.Source = bitmap;
        ShowVisual();
    }

    private void ShowVisual()
    {
        VisualScroller.Visibility = VisualToolbar.Visibility = Visibility.Visible;
        PreviewErrorPanel.Visibility = Visibility.Collapsed;
        FitVisualSize(VisualScroller.ActualWidth, VisualScroller.ActualHeight);
    }

    private void FitVisualSize(double width, double height)
    {
        if (width <= 2 || height <= 2 || _visualAspect <= 0) return;
        double imageWidth = Math.Min(width - 2, (height - 2) * _visualAspect);
        DocumentImage.Width = imageWidth;
        DocumentImage.Height = imageWidth / _visualAspect;
    }

    private void RenderText(bool code)
    {
        var paragraph = new Paragraph();
        if (!code || _text.Length == 0 || _text.Length > 20_000) paragraph.Inlines.Add(new Run { Text = _text.Length == 0 ? "（空文档）" : _text });
        else
        {
            try
            {
                var regex = new Regex("(?<comment>//[^\\n]*|/\\*[\\s\\S]*?\\*/|#[^\\n]*|<!--[\\s\\S]*?-->)|(?<text>\"(?:\\\\.|[^\"\\\\\\r\\n])*\"|'(?:\\\\.|[^'\\\\\\r\\n])*'|`(?:\\\\.|[^`\\\\])*`)|(?<number>\\b\\d+(?:\\.\\d+)?\\b)|(?<keyword>\\b(?:using|namespace|public|private|protected|internal|static|class|struct|record|interface|enum|void|var|let|const|func|function|def|return|if|else|for|foreach|while|switch|case|break|continue|new|async|await|try|catch|finally|throw|import|from|export|default|true|false|null|None|True|False|self|this|in|is|as|int|string|bool|double|extends|implements)\\b)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                int offset = 0;
                foreach (Match match in regex.Matches(_text))
                {
                    if (match.Index > offset) paragraph.Inlines.Add(new Run { Text = _text[offset..match.Index] });
                    string key = match.Groups["comment"].Success ? "FeigeCodeCommentBrush" : match.Groups["text"].Success ? "FeigeCodeStringBrush" : match.Groups["number"].Success ? "FeigeCodeNumberBrush" : "FeigeCodeKeywordBrush";
                    paragraph.Inlines.Add(new Run { Text = match.Value, Foreground = (Brush)Application.Current.Resources[key] });
                    offset = match.Index + match.Length;
                }
                if (offset < _text.Length) paragraph.Inlines.Add(new Run { Text = _text[offset..] });
            }
            catch (RegexMatchTimeoutException) { paragraph.Inlines.Clear(); paragraph.Inlines.Add(new Run { Text = _text }); }
        }
        PreviewText.Blocks.Add(paragraph);
    }

    private void UpdateWrap()
    {
        bool wrap = WrapPreviewButton.IsChecked == true;
        PreviewText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        PreviewText.MaxWidth = wrap ? Math.Max(100, PreviewLayout.Width - 64) : double.PositiveInfinity;
        LineNumbers.Visibility = !wrap && _format.Kind == PreviewKind.Code ? Visibility.Visible : Visibility.Collapsed;
        TextScroller.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Enabled;
        TextScroller.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void OnVisualSizeChanged(object sender, SizeChangedEventArgs e)
    {
        FitVisualSize(e.NewSize.Width, e.NewSize.Height);
    }

    private void Notice(string message) { PreviewNotice.Message = message; PreviewNotice.IsOpen = true; }
    private void ShowError(Exception ex)
    {
        PreviewLoading.IsActive = false;
        PreviewErrorText.Text = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "文件已移动或删除，暂时无法预览。",
            InvalidDataException => ex.Message,
            NotSupportedException => ex.Message,
            UnauthorizedAccessException => "无法读取此文件，请检查访问权限。",
            _ => "无法预览此文件，文件可能已损坏、加密或采用不支持的格式。"
        };
        PreviewErrorPanel.Visibility = Visibility.Visible;
        TextScroller.Visibility = VisualScroller.Visibility = Visibility.Collapsed;
        AppLog.Write("attachment preview: " + ex.GetType().Name);
    }
    private async void OnPreviousPageClick(object sender, RoutedEventArgs e) { if (_page == 0 || _rendering) return; _page--; await ChangePageAsync(); }
    private async void OnNextPageClick(object sender, RoutedEventArgs e) { if (_pdf is null || _page + 1 >= _pdf.PageCount || _rendering) return; _page++; await ChangePageAsync(); }
    private async Task ChangePageAsync() { try { await RenderPageAsync(); } catch (Exception ex) { if (!_closed) ShowError(ex); } }
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => VisualScroller.ChangeView(null, null, Math.Max(0.25f, VisualScroller.ZoomFactor / 1.25f));
    private void OnZoomInClick(object sender, RoutedEventArgs e) => VisualScroller.ChangeView(null, null, Math.Min(4, VisualScroller.ZoomFactor * 1.25f));
    private void OnFitClick(object sender, RoutedEventArgs e) => VisualScroller.ChangeView(0, 0, 1);
    private void OnWrapClick(object sender, RoutedEventArgs e) => UpdateWrap();
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try { var data = new DataPackage(); data.SetText(_text); Clipboard.SetContent(data); Notice("已复制当前预览文本。"); }
        catch { Notice("复制未完成，请稍后重试。"); }
    }
    private async void OnOpenFileClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        try { if (!await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(_path))) Notice("系统未能打开文件，请检查是否安装相应应用。"); }
        catch { Notice("无法打开文件，文件可能已移动或删除。"); }
    }
    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _closed = true; _cancel.Cancel();
        DocumentImage.Source = null; _pdf = null;
    }
}
