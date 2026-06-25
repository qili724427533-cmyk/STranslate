using ScreenGrab;
using System.Drawing;
using System.Windows;

namespace STranslate.Core;

public class Screenshot(Settings settings) : IScreenshot
{
    private const int DefaultCaptureDelayMs = 150;

    public Bitmap? GetScreenshot()
    {
        if (ScreenGrabber.IsCapturing)
            return default;
        var bitmap = ScreenGrabber.CaptureDialog(settings.ShowScreenshotAuxiliaryLines);
        if (bitmap == null)
            return default;
        return bitmap;
    }

    public async Task<Bitmap?> GetScreenshotAsync()
    {
        return await CaptureBitmapAsync();
    }

    public async Task<ScreenshotCaptureResult?> GetScreenshotCaptureAsync()
    {
        if (ScreenGrabber.IsCapturing)
            return default;

        if (App.Current.MainWindow.Visibility == Visibility.Visible &&
            !App.Current.MainWindow.Topmost)
            App.Current.MainWindow.Visibility = Visibility.Collapsed;

        // Allow UI to update before capturing
        await Task.Delay(DefaultCaptureDelayMs);

        // CaptureWithRegionAsync 直接回传截图选区的物理屏幕坐标，
        // 无需事后反推（旧版 CaptureAsync 只回传 bitmap）。
        var capture = await ScreenGrabber.CaptureWithRegionAsync(settings.ShowScreenshotAuxiliaryLines);
        if (capture == null)
            return default;

        return new ScreenshotCaptureResult(capture.Bitmap, capture.Region);
    }

    private async Task<Bitmap?> CaptureBitmapAsync()
    {
        if (ScreenGrabber.IsCapturing)
            return default;

        if (App.Current.MainWindow.Visibility == Visibility.Visible &&
            !App.Current.MainWindow.Topmost)
            App.Current.MainWindow.Visibility = Visibility.Collapsed;

        // Allow UI to update before capturing
        await Task.Delay(DefaultCaptureDelayMs);

        var bitmap = await ScreenGrabber.CaptureAsync(settings.ShowScreenshotAuxiliaryLines);
        if (bitmap == null)
            return default;

        return bitmap;
    }
}
