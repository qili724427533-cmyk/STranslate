using STranslate.Helpers;

namespace STranslate.Tests;

public class MainWindowAutoHidePolicyTests
{
    [Fact]
    public void ShouldHideWhenForegroundMovesToAnotherWindow()
    {
        var shouldHide = MainWindowAutoHidePolicy.ShouldHideOnForegroundChanged(
            hideWhenDeactivated: true,
            isTopmost: false,
            isVisible: true,
            mainWindowHandle: 0x100,
            foregroundWindowHandle: 0x200);

        Assert.True(shouldHide);
    }

    [Fact]
    public void ShouldNotHideWhenMainWindowIsStillForeground()
    {
        var shouldHide = MainWindowAutoHidePolicy.ShouldHideOnForegroundChanged(
            hideWhenDeactivated: true,
            isTopmost: false,
            isVisible: true,
            mainWindowHandle: 0x100,
            foregroundWindowHandle: 0x100);

        Assert.False(shouldHide);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ShouldNotHideWhenAutoHidePreconditionsAreNotMet(bool hideWhenDeactivated, bool isTopmost, bool isVisible)
    {
        var shouldHide = MainWindowAutoHidePolicy.ShouldHideOnForegroundChanged(
            hideWhenDeactivated,
            isTopmost,
            isVisible,
            mainWindowHandle: 0x100,
            foregroundWindowHandle: 0x200);

        Assert.False(shouldHide);
    }
}
