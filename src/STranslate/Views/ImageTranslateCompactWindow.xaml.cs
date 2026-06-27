using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Windows.Win32;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace STranslate.Views;

public partial class ImageTranslateCompactWindow
{
    private const double FallbackScreenWidthRatio = 0.85;
    private const double FallbackScreenHeightRatio = 0.85;
    private const double FallbackMinWidth = 320;
    private const double FallbackMinHeight = 180;
    private const double ToolbarReservedHeight = 64;
    // 按钮条宽度初值，仅用于首次 Measure 兜底；实际宽度在 PlaceOnPhysicalBounds 中实测。
    private const double ToolbarWidthFallback = 270;
    private const double GapH = 8;
    private const double GapV = 6;
    private const double WindowMargin = 8;

    private readonly ImageTranslateWindowViewModel _viewModel;
    private bool _isContextMenuOpen;
    private bool _isToolbarDropDownOpen;
    private bool _isClosing;
    private CompactWindowLayout? _layout;
    private DrawingRectangle? _pendingPhysicalBounds;

    public ImageTranslateCompactWindow()
    {
        _viewModel = Ioc.Default.GetRequiredService<ImageTranslateWindowViewModel>();
        DataContext = _viewModel;

        InitializeComponent();
    }

    public void PlaceForCapture(DrawingRectangle? physicalBounds, DrawingSize bitmapSize)
    {
        if (physicalBounds is { Width: > 0, Height: > 0 } bounds)
        {
            PlaceOnPhysicalBounds(bounds);
            return;
        }

        PlaceNearCursorScreen(bitmapSize);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32Helper.HideFromAltTab(this);

        if (_pendingPhysicalBounds is { } bounds)
        {
            _pendingPhysicalBounds = null;
            Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (_isContextMenuOpen || _isToolbarDropDownOpen || _isClosing)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isContextMenuOpen && !_isToolbarDropDownOpen && !_isClosing && IsVisible && !IsActive)
                Close();
        }, DispatcherPriority.Background);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _viewModel.CancelOperations();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 主动拆解视觉树：移除 InfoBar、SnackbarContainer 等控件并清空 Content，
        // 断开 WPF 静态 PropertyDescriptor._propertyMap 通过 PropertyChangeTracker
        // 对窗口内部控件的锚定，避免已关闭窗口被静态缓存钉死无法 GC。
        DetachVisualTree();

        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// 拆解精简窗口视觉树并释放控件引用。
    /// 精简窗口每次截图翻译都会新建并关闭，若不主动断开 InfoBar/SnackbarContainer 等
    /// 控件与窗口的连接，WPF 属性描述符静态表会通过 PropertyChangeTracker 反向持有窗口，
    /// 导致整窗（含 BitmapSource 原生帧缓冲）无法回收。
    /// </summary>
    private void DetachVisualTree()
    {
        if (Content is Panel panel)
        {
            // 清除每个子元素的数据上下文，断开控件到 VM 的引用
            for (int i = panel.Children.Count - 1; i >= 0; i--)
            {
                panel.Children[i].ClearValue(FrameworkElement.DataContextProperty);
            }
            panel.Children.Clear();
        }

        // 解除数据上下文与内容，断开 VM ↔ 视图的双向引用
        DataContext = null;
        Content = null;
    }

    private void PlaceOnPhysicalBounds(DrawingRectangle bounds)
    {
        var dpiScale = GetDpiScale(bounds);
        var workArea = GetPhysicalWorkArea(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

        // 实测按钮条所需宽度（DIP），避免常量估算偏差导致居中错位
        var toolbarWidthDip = MeasureToolbarWidthDip();

        _layout = ImageTranslateCompactWindowPlacement.CreateLayout(
            imageBounds: bounds,
            workArea: workArea,
            dpiScaleX: dpiScale.DpiScaleX,
            dpiScaleY: dpiScale.DpiScaleY,
            minWidthDip: MinWidth,
            minImageHeightDip: MinHeight,
            toolbarWidthDip: toolbarWidthDip,
            toolbarHeightDip: ToolbarReservedHeight,
            gapHDip: GapH,
            gapVDip: GapV,
            windowMarginDip: WindowMargin);

        PlaceOnPhysicalWindowBounds(_layout.WindowBounds, dpiScale);
        ApplyLayoutToVisualTree();
    }

    /// <summary>
    /// 实测按钮条 Border 所需 DIP 宽度。Measure 后取 DesiredSize.Width，
    /// 兜底用 ToolbarWidthFallback 常量。
    /// </summary>
    private double MeasureToolbarWidthDip()
    {
        PART_ToolbarBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = PART_ToolbarBorder.DesiredSize.Width;
        return width > 0 ? width : ToolbarWidthFallback;
    }

    /// <summary>
    /// 兜底定位：当无法拿到截图选区物理坐标时（理论上 CaptureWithRegionAsync 总会返回坐标，
    /// 仅在异常情况下为空），将窗口贴在光标左上方并约束在光标所在屏幕内。
    /// 不再依赖 ScreenshotSelectionResolver 反推选区位置。
    /// </summary>
    private void PlaceNearCursorScreen(DrawingSize bitmapSize)
    {
        if (!PInvoke.GetCursorPos(out var cursorPoint))
        {
            PlaceCenteredOnPrimaryScreen(bitmapSize);
            return;
        }

        var cursorPosition = new DrawingPoint(cursorPoint.X, cursorPoint.Y);

        // 找到光标所在屏幕，用于把窗口约束在屏幕内
        var screen = MonitorInfo.GetDisplayMonitors()
            .Select(monitor => new DrawingRectangle(
                (int)Math.Round(monitor.Bounds.X),
                (int)Math.Round(monitor.Bounds.Y),
                (int)Math.Round(monitor.Bounds.Width),
                (int)Math.Round(monitor.Bounds.Height)))
            .FirstOrDefault(s => s.Contains(cursorPosition));

        // 默认贴在光标左上方
        var left = cursorPosition.X - bitmapSize.Width;
        var top = cursorPosition.Y - bitmapSize.Height;

        // 约束在屏幕工作区内
        if (screen is { Width: > 0, Height: > 0 } s)
        {
            left = Math.Max(s.Left, Math.Min(left, s.Right - bitmapSize.Width));
            top = Math.Max(s.Top, Math.Min(top, s.Bottom - bitmapSize.Height));
        }

        PlaceOnPhysicalBounds(new DrawingRectangle(left, top, bitmapSize.Width, bitmapSize.Height));
    }

    private void PlaceCenteredOnPrimaryScreen(DrawingSize bitmapSize)
    {
        var screen = MonitorInfo.GetPrimaryDisplayMonitor();
        var workArea = new DrawingRectangle(
            (int)Math.Round(screen.WorkingArea.Left),
            (int)Math.Round(screen.WorkingArea.Top),
            (int)Math.Round(screen.WorkingArea.Width),
            (int)Math.Round(screen.WorkingArea.Height));
        var dpiScale = GetDpiScale(workArea);
        var windowBounds = ImageTranslateCompactWindowPlacement.CreateCenteredOnWorkArea(
            workArea,
            bitmapSize,
            dpiScale.DpiScaleX,
            dpiScale.DpiScaleY,
            FallbackMinWidth,
            FallbackMinHeight,
            ToolbarReservedHeight,
            FallbackScreenWidthRatio,
            FallbackScreenHeightRatio);

        PlaceOnPhysicalWindowBounds(windowBounds, dpiScale);
    }

    /// <summary>
    /// 两步定位(对齐 ScreenGrab 的做法):
    /// 1. 先用 DIP 写回 WPF 的 Left/Top/Width/Height,让布局系统认这个逻辑位置;
    /// 2. 再用 SetWindowPos 以物理像素精确校正 HWND。
    /// 只做第 2 步会导致 WPF 布局周期把窗口拉回逻辑坐标对应位置,在非 100% 缩放下产生偏移。
    /// </summary>
    private void PlaceOnPhysicalWindowBounds(DrawingRectangle bounds, System.Windows.DpiScale dpiScale)
    {
        var dipBounds = ImageTranslateCompactWindowPlacement.ToDipBounds(bounds, dpiScale.DpiScaleX, dpiScale.DpiScaleY);
        Left = dipBounds.Left;
        Top = dipBounds.Top;
        Width = dipBounds.Width;
        Height = dipBounds.Height;

        if (IsVisible)
            Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        else
            _pendingPhysicalBounds = bounds;
    }

    /// <summary>
    /// 把布局结果换算成 DIP 并应用到 ImageZoom、按钮条 Border 与执行遮罩：
    /// 固定 Width/Height 保证图片 1:1 显示，左上对齐 + Margin 把图片钉在选区绝对位置、
    /// 按钮条钉到布局算法算出的位置。按钮条 ZIndex 高于图片（XAML 内 PART_ToolbarBorder Panel.ZIndex=20）。
    /// 执行遮罩只覆盖图片区域（透明窗口下避免遮罩铺满整窗造成背景闪烁）。
    /// </summary>
    private void ApplyLayoutToVisualTree()
    {
        if (_layout is null) return;
        var dpi = GetDpiScale(_layout.WindowBounds);
        var sx = dpi.DpiScaleX;
        var sy = dpi.DpiScaleY;

        var imageWidthDip = _layout.ImageWidth / sx;
        var imageHeightDip = _layout.ImageHeight / sy;
        var imageMargin = new Thickness(_layout.ImageOffsetX / sx, _layout.ImageOffsetY / sy, 0, 0);

        // 图片：固定尺寸 + 左上对齐 + Margin 偏移（钉在选区绝对位置）
        PART_ImageZoom.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ImageZoom.VerticalAlignment = VerticalAlignment.Top;
        PART_ImageZoom.Width = imageWidthDip;
        PART_ImageZoom.Height = imageHeightDip;
        PART_ImageZoom.Margin = imageMargin;

        // 按钮条：固定尺寸 + 左上对齐 + Margin 偏移，ZIndex 高于图片
        PART_ToolbarBorder.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ToolbarBorder.VerticalAlignment = VerticalAlignment.Top;
        PART_ToolbarBorder.Width = _layout.ToolbarWidth / sx;
        PART_ToolbarBorder.Height = _layout.ToolbarHeight / sy;
        PART_ToolbarBorder.Margin = new Thickness(_layout.ToolbarX / sx, _layout.ToolbarY / sy, 0, 0);

        // 执行遮罩：只覆盖图片区域，与图片同尺寸同位置，避免铺满透明窗口造成背景闪烁
        PART_ExecutingOverlay.HorizontalAlignment = HorizontalAlignment.Left;
        PART_ExecutingOverlay.VerticalAlignment = VerticalAlignment.Top;
        PART_ExecutingOverlay.Width = imageWidthDip;
        PART_ExecutingOverlay.Height = imageHeightDip;
        PART_ExecutingOverlay.Margin = imageMargin;
    }

    private static System.Windows.DpiScale GetDpiScale(DrawingRectangle bounds) =>
        Win32Helper.GetDpiScaleForPhysicalPoint(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

    /// <summary>
    /// 获取包含指定物理坐标的屏幕工作区（物理像素）。
    /// MonitorInfo.Bounds/WorkingArea 直接来自 Win32 RECT，本身就是物理像素，无需 DPI 换算。
    /// </summary>
    private static DrawingRectangle GetPhysicalWorkArea(int physicalX, int physicalY)
    {
        var monitor = MonitorInfo.GetDisplayMonitors()
            .FirstOrDefault(m =>
            {
                var b = m.Bounds;
                return physicalX >= b.X && physicalX < b.X + b.Width
                    && physicalY >= b.Y && physicalY < b.Y + b.Height;
            }) ?? MonitorInfo.GetPrimaryDisplayMonitor();

        var w = monitor.WorkingArea;
        return new DrawingRectangle(
            (int)Math.Round(w.X),
            (int)Math.Round(w.Y),
            (int)Math.Round(w.Width),
            (int)Math.Round(w.Height));
    }

    private void OnImageContextMenuOpened(object sender, RoutedEventArgs e) => _isContextMenuOpen = true;

    private void OnImageContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        Activate();
    }

    private void OnToolbarComboBoxDropDownOpened(object sender, EventArgs e) => _isToolbarDropDownOpen = true;

    private void OnToolbarComboBoxDropDownClosed(object sender, EventArgs e)
    {
        _isToolbarDropDownOpen = false;
        if (!_isClosing && IsVisible)
            Activate();
    }
}
