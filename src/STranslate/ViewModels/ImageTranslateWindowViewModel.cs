using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using iNKORE.UI.WPF.Modern;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using STranslate.Controls;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.Services;
using STranslate.Plugin;
using STranslate.ViewModels.Pages;
using STranslate.Views;
using STranslate.Views.Pages;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Bitmap = System.Drawing.Bitmap;

namespace STranslate.ViewModels;

public partial class ImageTranslateWindowViewModel : ObservableObject, IDisposable
{
    #region Constructor & DI

    public ImageTranslateWindowViewModel(
        ILogger<ImageTranslateWindowViewModel> logger,
        Settings settings,
        HotkeySettings hotkeySettings,
        DataProvider dataProvider,
        MainWindowViewModel mainWindowViewModel,
        OcrService ocrService,
        TranslateService translateService,
        TtsService ttsService,
        Internationalization i18n,
        ISnackbar snackbar,
        INotification notification)
    {
        _logger = logger;
        Settings = settings;
        HotkeySettings = hotkeySettings;
        DataProvider = dataProvider;
        _mainWindowViewModel = mainWindowViewModel;
        _ocrService = ocrService;
        _translateService = translateService;
        _ttsService = ttsService;
        _i18n = i18n;
        _snackbar = snackbar;
        _notification = notification;

        OcrEngines = [];
        RefreshOcrEngines();
        RefreshSelectedOcrEngine();
        _transCollectionView = new() { Source = _translateService.Services };
        _transCollectionView.Filter += OnTransFilter;
        SelectedTranslateEngine = _translateService.ImageTranslateService;

        _ocrService.Services.CollectionChanged += OnOcrServicesCollectionChanged;
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged += OnOcrEnginePropertyChanged;
        }
        _ocrService.PropertyChanged += OnOcrServicePropertyChanged;

        // 监听图片翻译服务切换
        _translateService.PropertyChanged += OnTranServicePropertyChanged;

        Settings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private readonly ILogger<ImageTranslateWindowViewModel> _logger;

    #endregion

    #region Properties

    public Settings Settings { get; }
    public HotkeySettings HotkeySettings { get; }
    public DataProvider DataProvider { get; }

    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly OcrService _ocrService;
    private readonly TranslateService _translateService;
    private readonly TtsService _ttsService;
    private readonly Internationalization _i18n;
    private readonly ISnackbar _snackbar;
    private readonly INotification _notification;
    private const double WidthMultiplier = 2;
    private const double WidthAdjustment = 12;

    [ObservableProperty]
    public partial BitmapSource? DisplayImage { get; set; }

    [ObservableProperty]
    public partial bool IsShowingFitToWindow { get; set; } = false;

    [ObservableProperty]
    public partial bool IsExecuting { get; set; } = false;

    [ObservableProperty]
    public partial string ProcessRingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNoLocationInfoVisible { get; set; } = false;

    /// <summary>
    /// 翻译结果
    /// </summary>
    [ObservableProperty]
    public partial string Result { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ObservableCollection<OcrWord> OcrWords { get; set; } = [];

    /// <summary>
    /// 原始图像
    /// </summary>
    private BitmapSource? _sourceImage;

    /// <summary>
    /// 标注图像（显示识别边框）
    /// </summary>
    private BitmapSource? _annotatedImage;

    /// <summary>
    /// 结果图像（显示翻译文本）
    /// </summary>
    private BitmapSource? _resultImage;

    private OcrResult? _lastOcrResult;
    private ObservableCollection<OcrWord> _originalSelectionWords = [];
    private ObservableCollection<OcrWord> _translatedSelectionWords = [];

    [ObservableProperty]
    public partial ObservableCollection<Service> OcrEngines { get; set; }

    [ObservableProperty]
    public partial Service? SelectedOcrEngine { get; set; } = null;

    // 翻译服务过滤掉词典服务
    private readonly CollectionViewSource _transCollectionView;
    public ICollectionView TransCollectionView => _transCollectionView.View;

    [ObservableProperty]
    public partial Service? SelectedTranslateEngine { get; set; } = null;

    #endregion

    #region Commands

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ExecuteAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        if (IsExecuting) return;

        IsExecuting = true;
        ProcessRingText = _i18n.GetTranslation("RecognizingImageText");
        try
        {
            Clear();
            _sourceImage = Utilities.ToBitmapImage(bitmap, Settings.GetImageFormat());
            DisplayImage = _sourceImage;

            var ocrSvc = _ocrService.GetImageTranslateOcrSvcOrDefault();
            if (ocrSvc == null)
            {
                Helper.PromptConfigureService(
                    _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundTitle"),
                    _i18n.GetTranslation("ImageTranslateOcrServiceNotFoundMessage"),
                    nameof(OcrPage));
                return;
            }

            var data = Utilities.ToBytes(bitmap, Settings.GetImageFormat());
            _lastOcrResult = await ocrSvc.RecognizeAsync(
                new OcrRequest(data, Settings.OcrLanguage, bitmap.Width, bitmap.Height),
                cancellationToken);
            Utilities.PrepareOcrResult(_lastOcrResult);

            if (!_lastOcrResult.IsSuccess || string.IsNullOrEmpty(_lastOcrResult.Text))
            {
                _snackbar.ShowError(_i18n.GetTranslation("OcrFailed"));
                return;
            }


            if (Settings.CopyAfterOcr)
                ClipboardHelper.SetText(_lastOcrResult.Text);

            _originalSelectionWords = OcrWordBuilder.CreateFromOcrContents(_lastOcrResult.OcrContents);
            RefreshSelectableOcrWords();

            IsNoLocationInfoVisible = !Utilities.HasBoxPoints(_lastOcrResult);

            // 生成原始OCR标注图像（显示识别边框）
            //var originalAnnotatedImage = GenerateAnnotatedImage(_lastOcrResult, _sourceImage);

            // 分段逻辑
            var layoutBlocks = ApplyLayoutAnalysis(_lastOcrResult);

            // 生成分段后的标注图像（显示合并后的边框）
            _annotatedImage = GenerateAnnotatedImage(_lastOcrResult, _sourceImage);

            if (_translateService.ImageTranslateService?.Plugin is not ITranslatePlugin tranSvc)
            {
                _snackbar.ShowWarning(_i18n.GetTranslation("NoTranslateService"));
                return;
            }

            ProcessRingText = _i18n.GetTranslation("TranslatingText");

            await Parallel.ForEachAsync(layoutBlocks, cancellationToken, async (block, cancellationToken) =>
            {
                var (isSuccess, source, target) = await LanguageDetector
                    .GetLanguageAsync(
                        block.Text,
                        Settings.ImageTranslateSourceLang,
                        Settings.ImageTranslateTargetLang,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!isSuccess)
                {
                    _logger.LogWarning($"Language detection failed for text: {block.Text}");
                    _snackbar.ShowWarning(_i18n.GetTranslation("LanguageDetectionFailed"));
                    return;
                }
                if (string.IsNullOrWhiteSpace(block.Text))
                    return;

                var result = new TranslateResult();
                await tranSvc.TranslateAsync(new TranslateRequest(block.Text, source, target), result, cancellationToken);
                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                {
                    var normalizedText = ImageTranslateTextOverlayLayout.NormalizeOverlayText(result.Text);
                    if (!string.IsNullOrWhiteSpace(normalizedText))
                        block.Text = normalizedText;
                }
            });

            _lastOcrResult.OcrContents.Clear();
            _lastOcrResult.OcrContents.AddRange(layoutBlocks.Select(x => x.ToOcrContent()));

            // 生成翻译结果图像（在原图上覆盖翻译文本）
            _resultImage = GenerateTranslatedImage(layoutBlocks, Utilities.ToBitmapImage(bitmap, Settings.GetImageFormat()));
            Result = _lastOcrResult.Text;

            DisplayImage = Settings.IsImTranShowingAnnotated ? _annotatedImage : _resultImage;
            RefreshSelectableOcrWords();
        }
        catch (TaskCanceledException)
        {
            //TODO: 考虑提示用户取消操作
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("ImtransFailed")}\n{ex.Message}");
            _logger.LogError(ex, "Image Translate execution failed");
        }
        finally
        {
            IsExecuting = false;
        }
    }

    [RelayCommand]
    private async Task ImTransOcrAsync(Window? window)
    {
        window?.Hide();
        await Task.Delay(150);
        await _mainWindowViewModel.ScreenshotTranslateCommand.ExecuteAsync(null);
        window?.Show();
    }

    [RelayCommand]
    private async Task ReExecuteAsync()
    {
        if (_sourceImage == null || IsExecuting) return;
        using var bitmap = Utilities.ToBitmap(_sourceImage, Settings.GetBitmapEncoder());
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await _mainWindowViewModel.OpenSettingsInternalAsync(null);

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(OcrPage));

            if (SelectedOcrEngine != null)
                Ioc.Default.GetRequiredService<OcrViewModel>()
                    .SelectedItem = SelectedOcrEngine;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(TranslatePage));

            if (SelectedTranslateEngine != null)
                Ioc.Default.GetRequiredService<TranslateViewModel>()
                    .SelectedItem = SelectedTranslateEngine;
        }
        else
            Application.Current.Windows
                .OfType<SettingsWindow>()
                .First()
                .Navigate(nameof(StandalonePage));
    }

    [RelayCommand]
    private void SwitchImage() => Settings.IsImTranShowingAnnotated = !Settings.IsImTranShowingAnnotated;

    [RelayCommand]
    private void ToggleTextControl() => Settings.IsImTranShowingTextControl = !Settings.IsImTranShowingTextControl;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PlayAudioAsync(string text, CancellationToken cancellationToken)
    {
        var ttsSvc = _ttsService.GetActiveSvc<ITtsPlugin>();
        if (ttsSvc == null)
        {
            Helper.PromptConfigureService(
                _i18n.GetTranslation("Prompt"),
                _i18n.GetTranslation("TtsServiceNotFound"),
                nameof(TtsPage));
            return;
        }

        await ttsSvc.PlayAudioAsync(text, cancellationToken);
    }

    [RelayCommand]
    private void Copy(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            Clipboard.SetText(text);
            _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
        }
        else
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoCopyContent"));
        }
    }

    [RelayCommand]
    private void RemoveLineBreaks(TextBox textBox) =>
        Utilities.TransformText(textBox, t => t.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "));

    [RelayCommand]
    private void RemoveSpaces(TextBox textBox) =>
        Utilities.TransformText(textBox, t => t.Replace(" ", string.Empty));

    [RelayCommand]
    private void CopyImage(ImageZoom? imageZoom)
    {
        var text = imageZoom?.SelectedText;
        try
        {
            if (!string.IsNullOrEmpty(text))
            {
                Clipboard.SetText(text);
                _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
                return;
            }

            if (_sourceImage != null)
            {
                Clipboard.SetImage(_sourceImage);
                _snackbar.ShowSuccess(_i18n.GetTranslation("CopySuccess"));
                return;
            }

            _snackbar.ShowWarning(_i18n.GetTranslation("NoCopyContent"));
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("CopyFailed")}: {ex.Message}");
        }
    }

    [RelayCommand]
    private void SelectAllText(ImageZoom? imageZoom) => imageZoom?.SelectAllText();

    [RelayCommand]
    private void SaveImage()
    {
        if (_sourceImage is null)
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoImageToSave"));
            return;
        }

        var saveFileDialog = new SaveFileDialog
        {
            Title = _i18n.GetTranslation("SaveAs"),
            Filter = "PNG Files (*.png)|*.png|JPEG Files (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*",
            FileName = $"{DateTime.Now:yyyyMMddHHmmssfff}",
            DefaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            AddToRecent = true
        };

        if (saveFileDialog.ShowDialog() != true)
            return;

        try
        {
            BitmapEncoder encoder = saveFileDialog.FileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? new PngBitmapEncoder()
                        : new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_sourceImage));

            using var fs = new FileStream(saveFileDialog.FileName, FileMode.Create);
            encoder.Save(fs);

            _snackbar.ShowSuccess(_i18n.GetTranslation("SaveSuccess"));
        }
        catch (Exception ex)
        {
            _snackbar.ShowError($"{_i18n.GetTranslation("SaveFailed")}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenClipboardImageAsync()
    {
        var bitmapSource = Clipboard.GetImage();
        if (bitmapSource == null)
        {
            _snackbar.ShowWarning(_i18n.GetTranslation("NoImageInClipboard"));
            return;
        }

        using var bitmap = Utilities.ToBitmap(bitmapSource, Settings.GetBitmapEncoder());
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private async Task OpenImageFileAsync()
    {
        var openFileDialog = new OpenFileDialog
        {
            Title = _i18n.GetTranslation("ImportFromFile"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp",
            RestoreDirectory = true
        };

        if (openFileDialog.ShowDialog() != true) return;

        await using var fs = new FileStream(openFileDialog.FileName, FileMode.Open, FileAccess.Read);
        var bytes = new byte[fs.Length];
        _ = await fs.ReadAsync(bytes);

        using var bitmap = Utilities.ToBitmap(bytes);
        await ExecuteCommand.ExecuteAsync(bitmap);
    }

    [RelayCommand]
    private void ZoomOut(ImageZoom? element) => element?.ZoomOut();

    [RelayCommand]
    private void ZoomIn(ImageZoom? element) => element?.ZoomIn();

    [RelayCommand]
    private void FitToWindowSize(ImageZoom? element)
    {
        IsShowingFitToWindow = false;
        element?.Reset();
    }

    [RelayCommand]
    private void FitToActualSize(ImageZoom? element)
    {
        IsShowingFitToWindow = true;
        element?.ResetActualSize();
    }

    [RelayCommand]
    private void Cancel(Window window)
    {
        CancelOperations();
        window.Close();
    }

    #endregion

    #region Event Handlers

    private void OnTransFilter(object sender, FilterEventArgs e) => e.Accepted = e.Item is Service service && service.Plugin is ITranslatePlugin;

    // 添加标志位防止循环更新
    private bool _isUpdatingTranslateEngine = false;

    private void OnTranServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TranslateService.ImageTranslateService) ||
            _isUpdatingTranslateEngine)
            return;

        _isUpdatingTranslateEngine = true;
        try
        {
            SelectedTranslateEngine = _translateService.ImageTranslateService;
        }
        finally
        {
            _isUpdatingTranslateEngine = false;
        }
    }

    partial void OnSelectedTranslateEngineChanged(Service? value)
    {
        if (_isUpdatingTranslateEngine) return;

        _isUpdatingTranslateEngine = true;
        try
        {
            // 如果当前选中项被删除以后会自动触发选中临近的一项，关闭该VM绑定界面则没有该影响
            if (value == null)
                _translateService.DeactiveImTran();
            else
                _translateService.ActiveImTran(value);
        }
        finally
        {
            _isUpdatingTranslateEngine = false;
        }
    }

    private bool _isUpdatingOcrEngine = false;

    private void RefreshOcrEngines()
    {
        OcrEngines.Clear();
        foreach (var service in _ocrService.GetImageTranslateOcrServices())
            OcrEngines.Add(service);
    }

    private void RefreshSelectedOcrEngine()
    {
        _isUpdatingOcrEngine = true;
        try
        {
            SelectedOcrEngine = _ocrService.GetImageTranslateOcrServiceOrDefault();
        }
        finally
        {
            _isUpdatingOcrEngine = false;
        }
    }

    private void OnOcrServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OcrService.ImageTranslateOcrService))
        {
            RefreshSelectedOcrEngine();
        }
    }

    private void OnOcrServicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (Service service in e.NewItems)
            {
                service.PropertyChanged += OnOcrEnginePropertyChanged;
            }
        }
        if (e.OldItems != null)
        {
            foreach (Service service in e.OldItems)
            {
                service.PropertyChanged -= OnOcrEnginePropertyChanged;
            }
        }

        RefreshOcrEngines();
        RefreshSelectedOcrEngine();
    }

    private void OnOcrEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Service.IsEnabled) &&
            _ocrService.ImageTranslateOcrService == null)
        {
            RefreshSelectedOcrEngine();
        }
    }

    /// <summary>
    /// 图片翻译窗口中的 OCR 选择映射到独立的图片翻译 OCR 服务
    /// </summary>
    partial void OnSelectedOcrEngineChanged(Service? oldValue, Service? newValue)
    {
        if (_isUpdatingOcrEngine)
            return;

        if (newValue == null)
        {
            _ocrService.DeactiveImTranOcr();
        }
        else
        {
            if (_ocrService.IsImageTranslateOcrService(newValue))
                _ocrService.ActiveImTranOcr(newValue);
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Settings.IsImTranShowingTextControl):
                Settings.ImTranWindowWidth = Settings.IsImTranShowingTextControl
                        ? Settings.ImTranWindowWidth * WidthMultiplier - WidthAdjustment
                        : (Settings.ImTranWindowWidth + WidthAdjustment) / WidthMultiplier;
                break;
            case nameof(Settings.IsImTranShowingAnnotated):
                DisplayImage = Settings.IsImTranShowingAnnotated ? _annotatedImage : _resultImage;
                RefreshSelectableOcrWords();
                break;
        }
    }

    #endregion

    #region Private Methods

    private void Clear()
    {
        Result = string.Empty;
        _sourceImage = null;
        _annotatedImage = null;
        _resultImage = null;
        DisplayImage = null;
        _lastOcrResult = null;
        IsShowingFitToWindow = false;
        IsNoLocationInfoVisible = false;
        _originalSelectionWords = [];
        _translatedSelectionWords = [];
        OcrWords = [];
    }

    private void RefreshSelectableOcrWords()
    {
        OcrWords = Settings.IsImTranShowingAnnotated || _resultImage == null
            ? _originalSelectionWords
            : _translatedSelectionWords;
    }

    #endregion

    #region Image Translation

    /// <summary>
    /// 生成带有翻译文本覆盖的图像
    /// </summary>
    /// <param name="ocrResult">包含翻译后文本的OCR结果</param>
    /// <param name="image">原始图像</param>
    /// <returns>覆盖翻译文本后的图像</returns>
    private BitmapSource GenerateTranslatedImage(IReadOnlyList<OcrLayoutBlock> layoutBlocks, BitmapSource? image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (layoutBlocks.Count == 0 ||
            layoutBlocks.All(x => x.BoxPoints.Count == 0))
        {
            _translatedSelectionWords = [];
            return image;
        }

        // 获取源图像的 DPI 和缩放比例
        double dpiX = image.DpiX > 0 ? image.DpiX : 96;
        double dpiY = image.DpiY > 0 ? image.DpiY : 96;
        double pixelsPerDip = dpiX / 96.0;

        // ---------------------------------------------------------
        // 修复：针对小图进行超采样渲染 (Super-sampling)
        // 如果图片较小，强制放大渲染尺寸，以保证文字矢量绘制的清晰度
        // ---------------------------------------------------------
        double scaleFactor = 1.0;
        double minDimension = Math.Min(image.PixelWidth, image.PixelHeight);
        
        // 如果最小边小于 1000 像素，进行放大，最大放大倍数为 4 倍
        if (minDimension < 1000)
        {
            scaleFactor = Math.Min(4.0, 1000.0 / minDimension);
            // 确保至少放大 2 倍以获得较好的抗锯齿效果
            scaleFactor = Math.Max(scaleFactor, 2.0);
        }

        // 计算渲染目标尺寸
        int renderWidth = (int)(image.PixelWidth * scaleFactor);
        int renderHeight = (int)(image.PixelHeight * scaleFactor);

        var overlayTheme = GetOverlayTheme();
        var measureTextBrush = new SolidColorBrush(Colors.Black);
        measureTextBrush.Freeze();
        var overlays = layoutBlocks
            .Where(item => item.BoxPoints.Count > 0 && !string.IsNullOrEmpty(item.Text))
            .Select(item => CreateTranslatedTextOverlay(item, overlayTheme, pixelsPerDip, measureTextBrush))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();

        _translatedSelectionWords = OcrWordBuilder.CreateIndexedCollection(
            overlays.SelectMany(overlay =>
                OcrWordBuilder.CreateFromFormattedText(
                    overlay.Text,
                    overlay.FormattedText,
                    overlay.TextPosition,
                    overlay.Plan.TextClipRect,
                    scaleFactor)),
            preserveOrder: true);

        var drawingVisual = new DrawingVisual();

        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // 关键修复：应用缩放变换
            // 1. pixelsPerDip: 抵消 WPF DPI 缩放，回归物理像素坐标
            // 2. scaleFactor: 应用超采样缩放
            double totalScale = scaleFactor / pixelsPerDip;
            drawingContext.PushTransform(new ScaleTransform(totalScale, totalScale));

            // 绘制原始图像
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));

            // 先统一绘制覆盖背景，再绘制译文，避免后续背景覆盖前面已经画好的译文。
            foreach (var overlay in overlays)
            {
                var backgroundBrush = new SolidColorBrush(overlay.Plan.OverlayBackgroundColor);
                backgroundBrush.Freeze();

                drawingContext.DrawRoundedRectangle(
                    backgroundBrush,
                    null,
                    overlay.Plan.OverlayRect,
                    overlay.Plan.CornerRadius,
                    overlay.Plan.CornerRadius);
            }

            foreach (var overlay in overlays)
                DrawTranslatedTextOverlay(drawingContext, overlay);
            
            // 恢复变换
            drawingContext.Pop();
        }

        // 关键修复：使用源图像的 DPI，但尺寸是放大后的
        var renderBitmap = new RenderTargetBitmap(
            renderWidth,
            renderHeight,
            dpiX,
            dpiY,
            PixelFormats.Pbgra32
        );

        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    /// <summary>
    /// 在指定区域绘制翻译文本覆盖层
    /// </summary>
    /// <param name="drawingContext">绘图上下文</param>
    /// <param name="content">包含翻译文本和位置信息的内容</param>
    /// <param name="pixelsPerDip">DPI缩放比例</param>
    private TranslatedTextOverlay? CreateTranslatedTextOverlay(
        OcrLayoutBlock content,
        ImageTranslateOverlayTheme overlayTheme,
        double pixelsPerDip,
        Brush measureTextBrush)
    {
        var boundingRect = CalculateBoundingRect(content.BoxPoints);
        if (boundingRect.IsEmpty || boundingRect.Width <= 0 || boundingRect.Height <= 0)
            return null;

        var plan = ImageTranslateTextOverlayLayout.Create(
            content,
            boundingRect,
            (fontSize, textRect, isMultiLine) => MeasureFormattedText(
                content.Text,
                fontSize,
                textRect.Width,
                measureTextBrush,
                pixelsPerDip,
                isMultiLine ? fontSize * 1.28 : 0,
                isMultiLine ? 0 : 1),
            overlayTheme);

        var textBrush = new SolidColorBrush(plan.ForegroundColor);
        textBrush.Freeze();
        var shadowBrush = new SolidColorBrush(CreateTextShadowColor(plan.ForegroundColor));
        shadowBrush.Freeze();

        var formattedText = CreateFormattedText(
            content.Text,
            plan.FontSize,
            textBrush,
            plan.TextRect.Width,
            plan.MaxTextHeight,
            plan.LineHeight,
            plan.ShouldTrim || !plan.IsMultiLine,
            pixelsPerDip,
            plan.MaxLineCount);
        var shadowText = CreateFormattedText(
            content.Text,
            plan.FontSize,
            shadowBrush,
            plan.TextRect.Width,
            plan.MaxTextHeight,
            plan.LineHeight,
            plan.ShouldTrim || !plan.IsMultiLine,
            pixelsPerDip,
            plan.MaxLineCount);

        var textPosition = new Point(
            plan.TextRect.Left,
            plan.IsMultiLine
                ? plan.TextRect.Top
                : plan.TextClipRect.Top + Math.Max(0, (plan.TextClipRect.Height - formattedText.Height) / 2)
        );

        return new TranslatedTextOverlay(content.Text, plan, shadowText, formattedText, textPosition);
    }

    private static void DrawTranslatedTextOverlay(DrawingContext drawingContext, TranslatedTextOverlay overlay)
    {
        drawingContext.PushClip(new RectangleGeometry(overlay.Plan.TextClipRect));
        drawingContext.DrawText(
            overlay.ShadowText,
            new Point(overlay.TextPosition.X + 0.75, overlay.TextPosition.Y + 0.75));
        drawingContext.DrawText(overlay.FormattedText, overlay.TextPosition);
        drawingContext.Pop();
    }

    /// <summary>
    /// 测量换行文本的实际占用尺寸。
    /// </summary>
    internal static Size MeasureFormattedText(
        string text,
        double fontSize,
        double maxWidth,
        Brush textBrush,
        double pixelsPerDip,
        double lineHeight = 0,
        int maxLineCount = 0)
    {
        var measureWidth = ShouldMeasureNaturalSingleLine(lineHeight, maxLineCount)
            ? CreateSingleLineMeasureWidth(text, fontSize, maxWidth)
            : maxWidth;
        var formattedText = CreateFormattedText(
            text,
            fontSize,
            textBrush,
            measureWidth,
            double.PositiveInfinity,
            lineHeight,
            false,
            pixelsPerDip,
            maxLineCount);

        return new Size(GetMeasuredTextWidth(formattedText), formattedText.Height);
    }

    /// <summary>
    /// 创建格式化文本对象。
    /// </summary>
    internal static FormattedText CreateFormattedText(
        string text,
        double fontSize,
        Brush textBrush,
        double maxWidth,
        double maxHeight,
        double lineHeight,
        bool shouldTrim,
        double pixelsPerDip,
        int maxLineCount = 0)
    {
        var formattedText = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei, Arial, SimSun"),
            fontSize,
            textBrush,
            pixelsPerDip); // 关键修复：使用正确的缩放比例，而不是硬编码的 96

        formattedText.MaxTextWidth = maxWidth;
        if (!double.IsPositiveInfinity(maxHeight))
            formattedText.MaxTextHeight = Math.Max(maxHeight, lineHeight);
        if (lineHeight > 0)
            formattedText.LineHeight = lineHeight;
        if (maxLineCount > 0)
            formattedText.MaxLineCount = maxLineCount;
        formattedText.TextAlignment = TextAlignment.Left;
        formattedText.Trimming = shouldTrim ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        return formattedText;
    }

    private static bool ShouldMeasureNaturalSingleLine(double lineHeight, int maxLineCount) =>
        maxLineCount == 1 && lineHeight <= 0;

    private static double CreateSingleLineMeasureWidth(string text, double fontSize, double maxWidth)
    {
        var estimatedWidth = Math.Max(text.Length, 1) * Math.Max(fontSize, 1) * 2;
        return Math.Clamp(Math.Max(maxWidth, estimatedWidth), 1, 100_000);
    }

    private static double GetMeasuredTextWidth(FormattedText formattedText) =>
        Math.Max(formattedText.Width, formattedText.WidthIncludingTrailingWhitespace);

    private sealed record TranslatedTextOverlay(
        string Text,
        ImageTranslateTextOverlayPlan Plan,
        FormattedText ShadowText,
        FormattedText FormattedText,
        Point TextPosition);

    private ImageTranslateOverlayTheme GetOverlayTheme() =>
        Settings.ColorScheme == ElementTheme.Dark
            ? ImageTranslateOverlayTheme.Dark
            : ImageTranslateOverlayTheme.Light;

    private static Color CreateTextShadowColor(Color foregroundColor) =>
        IsLightColor(foregroundColor)
            ? Color.FromArgb(120, 0, 0, 0)
            : Color.FromArgb(120, 255, 255, 255);

    private static bool IsLightColor(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d >= 0.5;

    #endregion

    #region Border Drawing

    private BitmapSource GenerateAnnotatedImage(OcrResult ocrResult, BitmapSource? image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (!Utilities.HasBoxPoints(ocrResult))
            return image;

        var drawingVisual = new DrawingVisual();

        using (var drawingContext = drawingVisual.RenderOpen())
        {
            // 绘制原始图像
            drawingContext.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));

            // 创建并冻结画笔以提高性能
            var pen = new Pen(Brushes.Red, 2);
            pen.Freeze();

            // 绘制所有多边形
            foreach (var item in ocrResult.OcrContents)
            {
                if (item.BoxPoints == null || item.BoxPoints.Count == 0)
                    continue;

                var geometry = CreatePolygonGeometry(item.BoxPoints);
                drawingContext.DrawGeometry(null, pen, geometry);
            }
        }

        // 使用标准 96 DPI，Viewbox 会自动处理高 DPI 屏幕的缩放
        var renderBitmap = new RenderTargetBitmap(
            image.PixelWidth,
            image.PixelHeight,
            96,
            96,
            PixelFormats.Pbgra32
        );

        renderBitmap.Render(drawingVisual);
        renderBitmap.Freeze();

        return renderBitmap;
    }

    private static StreamGeometry CreatePolygonGeometry(List<BoxPoint> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, points[0].Y), false, true);

            for (int i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(points[i].X, points[i].Y), true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    #endregion

    #region Text Segmentation

    /// <summary>
    /// 应用分段逻辑，直接修改 OCR 结果中的内容分组。
    /// </summary>
    /// <param name="ocrResult">OCR 识别结果</param>
    private List<OcrLayoutBlock> ApplyLayoutAnalysis(OcrResult ocrResult)
    {
#if DEBUG
        var originalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"原始文本块数量: {originalCount}");
#endif

        var layoutBlocks = OcrLayoutAnalyzer.AnalyzeBlocks(ocrResult, Settings.LayoutAnalysisMode);
        ocrResult.OcrContents.Clear();
        ocrResult.OcrContents.AddRange(layoutBlocks.Select(x => x.ToOcrContent()));

#if DEBUG
        var finalCount = ocrResult.OcrContents.Count;
        System.Diagnostics.Debug.WriteLine($"合并后文本块数量: {finalCount}");
        if (originalCount > 0)
            System.Diagnostics.Debug.WriteLine($"合并率: {(originalCount - finalCount) / (double)originalCount * 100:F1}%");

        // 输出每个合并后的文本块
        for (int i = 0; i < layoutBlocks.Count; i++)
        {
            var block = layoutBlocks[i];
            System.Diagnostics.Debug.WriteLine(
                $"文本块 {i + 1}: [{block.Source}] confidence={block.Confidence:F2}, lines={block.LineBoxPoints.Count}, text={block.Text}");
        }
#endif

        return layoutBlocks;
    }

    /// <summary>
    /// 根据坐标点计算边界矩形
    /// </summary>
    private static Rect CalculateBoundingRect(List<BoxPoint> boxPoints)
    {
        if (boxPoints == null || boxPoints.Count == 0)
            return Rect.Empty;

        var minX = boxPoints.Min(p => p.X);
        var minY = boxPoints.Min(p => p.Y);
        var maxX = boxPoints.Max(p => p.X);
        var maxY = boxPoints.Max(p => p.Y);

        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    #endregion

    #region Cancel & IDisposable

    public void CancelOperations()
    {
        ExecuteCancelCommand.Execute(null);
        PlayAudioCancelCommand.Execute(null);
        Clear();
    }

    public void Dispose()
    {
        // 取消订阅事件，防止内存泄漏
        _ocrService.Services.CollectionChanged -= OnOcrServicesCollectionChanged;
        _ocrService.PropertyChanged -= OnOcrServicePropertyChanged;
        foreach (var service in _ocrService.Services)
        {
            service.PropertyChanged -= OnOcrEnginePropertyChanged;
        }
        _transCollectionView.Filter -= OnTransFilter;
        _translateService.PropertyChanged -= OnTranServicePropertyChanged;
        Settings.PropertyChanged -= OnSettingsPropertyChanged;
    }

    #endregion
}
