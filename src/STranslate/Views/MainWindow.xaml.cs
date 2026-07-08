using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.Extensions.Logging;
using STranslate.Core;
using STranslate.Helpers;
using STranslate.ViewModels;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace STranslate.Views;

public partial class MainWindow : IDisposable
{
    private const int WmNcHitTest = 0x0084;
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;

    private static readonly IntPtr HtClient = new(1);
    private static readonly IntPtr HtLeft = new(10);
    private static readonly IntPtr HtRight = new(11);

    private readonly MainWindowViewModel _viewModel;
    private readonly Settings _settings;
    private readonly ILogger<MainWindow> _logger;
    private readonly WinEventProc _foregroundChangedProc;
    private bool _disposed = false;
    private HwndSource? _hwndSource;
    private nint _foregroundChangedHook;

    public MainWindow()
    {
        _viewModel = Ioc.Default.GetRequiredService<MainWindowViewModel>();
        _settings = Ioc.Default.GetRequiredService<Settings>();
        _logger = Ioc.Default.GetRequiredService<ILogger<MainWindow>>();
        _foregroundChangedProc = OnForegroundChanged;

        DataContext = _viewModel;

        InitializeComponent();

        //Notification.Show("STranslate", "Welcome to STranslate!");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.InitializeWindowLayoutConstraints();
        _viewModel.UpdatePosition(_settings.HideOnStartup);

        _hwndSource = Win32Helper.AddWndProcHook(this, WndProc);
        RegisterForegroundChangedHook();
    }


    protected override void OnContentRendered(EventArgs e)
    {
        if (_settings.HideOnStartup)
        {
            _viewModel.Hide();
        }
        else
        {
            _viewModel.Show();
            Win32Helper.SetForegroundWindow(this);
        }

        base.OnContentRendered(e);
    }

    protected override void OnDeactivated(EventArgs e)
    {
        if (_viewModel.IsTopmost) return;

        // win32 api和wpf层面修改窗口显隐时表现有所不同，直接使用Hide可能会导致出现在Alt-Tab栏
        // https://github.com/ZGGSONG/STranslate/issues/165
        if (_settings.HideWhenDeactivated)
            _viewModel.Hide();

        base.OnDeactivated(e);
    }

    private void OnClosed(object sender, EventArgs e)
    {
        UnregisterForegroundChangedHook();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Helper.TaskbarCreatedMessage)
        {
            Dispatcher.BeginInvoke(RefreshNotifyIcon, DispatcherPriority.Loaded);
        }

        if (msg == WmNcHitTest && TryHandleHorizontalResizeHitTest(lParam, out var hitTestResult))
        {
            handled = true;
            return hitTestResult;
        }

        return IntPtr.Zero;
    }

    private void RegisterForegroundChangedHook()
    {
        if (_foregroundChangedHook != 0)
            return;

        _foregroundChangedHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            _foregroundChangedProc,
            0,
            0,
            WinEventOutOfContext);

        if (_foregroundChangedHook == 0)
        {
            _logger.LogWarning("Failed to register foreground changed hook.");
        }
    }

    private void UnregisterForegroundChangedHook()
    {
        if (_foregroundChangedHook == 0)
            return;

        if (!UnhookWinEvent(_foregroundChangedHook))
            _logger.LogWarning("Failed to unregister foreground changed hook.");

        _foregroundChangedHook = 0;
    }

    private void OnForegroundChanged(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        Dispatcher.BeginInvoke(() => HandleForegroundChanged(hwnd), DispatcherPriority.Background);
    }

    private void HandleForegroundChanged(nint foregroundWindowHandle)
    {
        var mainWindowHandle = new WindowInteropHelper(this).Handle;
        if (!MainWindowAutoHidePolicy.ShouldHideOnForegroundChanged(
                _settings.HideWhenDeactivated,
                _viewModel.IsTopmost,
                Visibility == Visibility.Visible,
                mainWindowHandle,
                foregroundWindowHandle))
            return;

        _viewModel.Hide();
    }

    private delegate void WinEventProc(
        nint hWinEventHook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint hmodWinEventProc,
        WinEventProc pfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hWinEventHook);

    private bool TryHandleHorizontalResizeHitTest(IntPtr lParam, out IntPtr hitTestResult)
    {
        hitTestResult = IntPtr.Zero;

        var hwnd = Win32Helper.GetWindowHandle(this);
        if (!PInvoke.GetWindowRect(hwnd, out var windowRect))
            return false;

        var cursorX = GetSignedLowWord(lParam);
        var cursorY = GetSignedHighWord(lParam);
        var resizeBorder = GetResizeBorderThickness();

        var isLeftBorder = cursorX >= windowRect.left && cursorX < windowRect.left + resizeBorder.Width;
        var isRightBorder = cursorX <= windowRect.right && cursorX > windowRect.right - resizeBorder.Width;
        if (isLeftBorder)
        {
            hitTestResult = HtLeft;
            return true;
        }

        if (isRightBorder)
        {
            hitTestResult = HtRight;
            return true;
        }

        var isTopBorder = cursorY >= windowRect.top && cursorY < windowRect.top + resizeBorder.Height;
        var isBottomBorder = cursorY <= windowRect.bottom && cursorY > windowRect.bottom - resizeBorder.Height;
        if (isTopBorder || isBottomBorder)
        {
            // 高度交给 SizeToContent 跟随内容变化，边缘命中退回 client 可阻止手动纵向 resize。
            hitTestResult = HtClient;
            return true;
        }

        return false;
    }

    private static Size GetResizeBorderThickness()
    {
        var paddedBorder = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXPADDEDBORDER);
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSIZEFRAME) + paddedBorder;
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSIZEFRAME) + paddedBorder;

        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    private static int GetSignedLowWord(IntPtr value) => unchecked((short)((long)value & 0xffff));

    private static int GetSignedHighWord(IntPtr value) => unchecked((short)(((long)value >> 16) & 0xffff));

    private void RefreshNotifyIcon()
    {
        var shouldHide = _settings.HideNotifyIcon;

        // 如果配置显示托盘图标，则不需要刷新
        if (!shouldHide) return;

        _settings.HideNotifyIcon = false;
        _settings.HideNotifyIcon = shouldHide;
    }

    #region IDisposable

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _hwndSource?.Dispose();
                PART_NotifyIcon.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion
}
