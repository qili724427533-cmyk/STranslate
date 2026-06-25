using CommunityToolkit.Mvvm.DependencyInjection;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.ComponentModel;
using System.Windows;
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

    private readonly ImageTranslateWindowViewModel _viewModel;
    private bool _isContextMenuOpen;
    private bool _isClosing;

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
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        if (_isContextMenuOpen || _isClosing)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isContextMenuOpen && !_isClosing && IsVisible && !IsActive)
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
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void PlaceOnPhysicalBounds(DrawingRectangle bounds)
    {
        var dpiScale = GetDpiScale(bounds);
        var windowBounds = ImageTranslateCompactWindowPlacement.CreateForImageBounds(
            bounds,
            dpiScale.DpiScaleX,
            dpiScale.DpiScaleY,
            MinWidth,
            MinHeight,
            ToolbarReservedHeight);

        PlaceOnPhysicalWindowBounds(windowBounds, dpiScale);
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

        Win32Helper.SetWindowPhysicalBounds(this, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
    }

    private static System.Windows.DpiScale GetDpiScale(DrawingRectangle bounds) =>
        Win32Helper.GetDpiScaleForPhysicalPoint(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);

    private void OnImageContextMenuOpened(object sender, RoutedEventArgs e) => _isContextMenuOpen = true;

    private void OnImageContextMenuClosed(object sender, RoutedEventArgs e)
    {
        _isContextMenuOpen = false;
        Activate();
    }
}
