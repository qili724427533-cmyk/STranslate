namespace STranslate.Helpers;

public static class MainWindowAutoHidePolicy
{
    public static bool ShouldHideOnForegroundChanged(
        bool hideWhenDeactivated,
        bool isTopmost,
        bool isVisible,
        nint mainWindowHandle,
        nint foregroundWindowHandle)
    {
        return hideWhenDeactivated &&
               !isTopmost &&
               isVisible &&
               mainWindowHandle != 0 &&
               foregroundWindowHandle != 0 &&
               mainWindowHandle != foregroundWindowHandle;
    }
}
