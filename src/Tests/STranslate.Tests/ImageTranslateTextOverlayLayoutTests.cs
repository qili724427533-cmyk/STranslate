using STranslate.Core;
using STranslate.Plugin;
using System.Windows;
using System.Windows.Media;

namespace STranslate.Tests;

public class ImageTranslateTextOverlayLayoutTests
{
    [Fact]
    public void MultilineParagraphFontSizeIsLimitedByMedianLineHeight()
    {
        var block = Block(
            Box(0, 0, 1000, 150),
            Box(0, 0, 900, 30),
            Box(0, 36, 900, 30),
            Box(0, 72, 900, 30));

        var plan = CreatePlan(block, new Rect(0, 0, 1000, 150), (_, _) => new Size(20, 20));

        Assert.True(plan.IsMultiLine);
        Assert.Equal(30 * 0.90, plan.FontSize, precision: 3);
    }

    [Fact]
    public void SingleLineTitleFontSizeUsesSingleLineLimitAndGlobalMaximum()
    {
        var block = Block(
            Box(0, 0, 400, 80),
            Box(0, 0, 380, 50));

        var plan = CreatePlan(block, new Rect(0, 0, 400, 80), (_, _) => new Size(20, 20));

        Assert.False(plan.IsMultiLine);
        Assert.Equal(ImageTranslateTextOverlayPlan.MaxFontSize, plan.FontSize);
    }

    [Fact]
    public void EraseRectsExpandLineBoxesAndUseAdaptiveOverlayBackground()
    {
        var block = Block(
            Box(0, 0, 200, 60),
            Box(10, 20, 100, 20));

        var plan = CreatePlan(block, new Rect(0, 0, 200, 60), (_, _) => new Size(10, 10));

        Assert.Equal(Color.FromArgb(210, 255, 255, 255), plan.OverlayBackgroundColor);
        Assert.Equal(Colors.Black, plan.ForegroundColor);
        Assert.Single(plan.EraseRects);
        Assert.Equal(8, plan.EraseRects[0].Left, precision: 3);
        Assert.Equal(16.4, plan.EraseRects[0].Top, precision: 3);
        Assert.Equal(104, plan.EraseRects[0].Width, precision: 3);
        Assert.Equal(27.2, plan.EraseRects[0].Height, precision: 3);
    }

    [Fact]
    public void SingleLineTextClipRectCoversExpandedEraseRect()
    {
        var block = Block(
            Box(10, 20, 100, 20),
            Box(10, 20, 100, 20));

        var plan = CreatePlan(block, new Rect(10, 20, 100, 20), (_, _) => new Size(10, 10));

        Assert.Equal(8, plan.TextClipRect.Left, precision: 3);
        Assert.Equal(16.4, plan.TextClipRect.Top, precision: 3);
        Assert.Equal(104, plan.TextClipRect.Width, precision: 3);
        Assert.Equal(27.2, plan.TextClipRect.Height, precision: 3);
        AssertCovers(plan.TextClipRect, plan.BoundingRect);
        AssertCovers(plan.TextClipRect, plan.EraseRects[0]);
        AssertCovers(plan.OverlayRect, plan.TextClipRect);
    }

    [Fact]
    public void SingleLineUsesNaturalTextHeightAndExpandedFitHeight()
    {
        var measuredRects = new List<Rect>();
        var block = Block(
            Box(10, 20, 100, 20),
            Box(10, 20, 100, 20));

        var plan = ImageTranslateTextOverlayLayout.Create(
            block,
            new Rect(10, 20, 100, 20),
            (_, textRect, _) =>
            {
                measuredRects.Add(textRect);
                return new Size(10, 10);
            });

        Assert.Equal(0, plan.LineHeight);
        Assert.Equal(1, plan.MaxLineCount);
        Assert.True(double.IsPositiveInfinity(plan.MaxTextHeight));
        Assert.All(measuredRects, rect => Assert.Equal(plan.TextClipRect.Height, rect.Height, precision: 3));
    }

    [Fact]
    public void MultilineTextClipRectCoversAllExpandedEraseRects()
    {
        var block = Block(
            Box(10, 20, 180, 46),
            Box(10, 20, 180, 20),
            Box(10, 46, 180, 20));

        var plan = CreatePlan(block, new Rect(10, 20, 180, 46), (_, _) => new Size(10, 10));

        AssertCovers(plan.TextClipRect, plan.BoundingRect);
        Assert.All(plan.EraseRects, eraseRect => AssertCovers(plan.TextClipRect, eraseRect));
        Assert.All(plan.EraseRects, eraseRect => AssertCovers(plan.OverlayRect, eraseRect));
        Assert.True(plan.TextClipRect.Top < plan.BoundingRect.Top);
        Assert.True(plan.TextClipRect.Bottom > plan.BoundingRect.Bottom);
    }

    [Fact]
    public void MultilineKeepsExplicitLineHeightAndTextHeightLimit()
    {
        var block = Block(
            Box(10, 20, 180, 46),
            Box(10, 20, 180, 20),
            Box(10, 46, 180, 20));

        var plan = CreatePlan(block, new Rect(10, 20, 180, 46), (_, _) => new Size(10, 10));

        Assert.True(plan.LineHeight > 0);
        Assert.Equal(0, plan.MaxLineCount);
        Assert.Equal(plan.TextRect.Height, plan.MaxTextHeight);
    }

    [Fact]
    public void CreatePassesLineModeToMeasureText()
    {
        var singleLineModes = new List<bool>();
        var multilineModes = new List<bool>();
        var singleLineBlock = Block(
            Box(0, 0, 100, 20),
            Box(0, 0, 100, 20));
        var multilineBlock = Block(
            Box(0, 0, 100, 46),
            Box(0, 0, 100, 20),
            Box(0, 26, 100, 20));

        ImageTranslateTextOverlayLayout.Create(
            singleLineBlock,
            new Rect(0, 0, 100, 20),
            (_, _, isMultiLine) =>
            {
                singleLineModes.Add(isMultiLine);
                return new Size(10, 10);
            });
        ImageTranslateTextOverlayLayout.Create(
            multilineBlock,
            new Rect(0, 0, 100, 46),
            (_, _, isMultiLine) =>
            {
                multilineModes.Add(isMultiLine);
                return new Size(10, 10);
            });

        Assert.All(singleLineModes, mode => Assert.False(mode));
        Assert.All(multilineModes, mode => Assert.True(mode));
    }

    [Fact]
    public void TextRectStaysInsideParagraphBoundsWithPadding()
    {
        var block = Block(
            Box(10, 20, 200, 100),
            Box(10, 20, 180, 20),
            Box(10, 46, 180, 20));

        var plan = CreatePlan(block, new Rect(10, 20, 200, 100), (_, _) => new Size(10, 10));

        Assert.Equal(11, plan.TextRect.Left);
        Assert.Equal(21, plan.TextRect.Top);
        Assert.Equal(209, plan.TextRect.Right);
        Assert.Equal(119, plan.TextRect.Bottom);
    }

    [Fact]
    public void OversizedTextAtMinimumFontSizeIsMarkedForTrimming()
    {
        var block = Block(
            Box(0, 0, 80, 20),
            Box(0, 0, 80, 20));

        var plan = CreatePlan(block, new Rect(0, 0, 80, 20), (_, textRect) => new Size(textRect.Width + 1, textRect.Height + 1));

        Assert.True(plan.ShouldTrim);
        Assert.Equal(ImageTranslateTextOverlayPlan.MinFontSize, plan.FontSize);
    }

    [Fact]
    public void MissingLineBoxesFallsBackToParagraphBoxForErase()
    {
        var block = Block(Box(5, 6, 70, 30));
        var boundingRect = new Rect(5, 6, 70, 30);

        var plan = CreatePlan(block, boundingRect, (_, _) => new Size(10, 10));

        Assert.False(plan.IsMultiLine);
        Assert.Equal(boundingRect, plan.EraseRects[0]);
        Assert.Equal(boundingRect, plan.TextClipRect);
        Assert.Equal(boundingRect, plan.OverlayRect);
        Assert.Equal(30 * 1.08, plan.FontSize, precision: 3);
    }

    [Fact]
    public void LightBackgroundUsesLightOverlayAndBlackText()
    {
        var block = Block(
            Box(0, 0, 200, 40),
            Box(0, 0, 180, 20));

        var plan = ImageTranslateTextOverlayLayout.Create(
            block,
            new Rect(0, 0, 200, 40),
            (_, _, _) => new Size(10, 10),
            ImageTranslateBackgroundSample.Light);

        Assert.Equal(Color.FromArgb(210, 255, 255, 255), plan.OverlayBackgroundColor);
        Assert.Equal(Colors.Black, plan.ForegroundColor);
    }

    [Fact]
    public void DarkBackgroundUsesDarkOverlayAndWhiteText()
    {
        var block = Block(
            Box(0, 0, 200, 40),
            Box(0, 0, 180, 20));

        var plan = ImageTranslateTextOverlayLayout.Create(
            block,
            new Rect(0, 0, 200, 40),
            (_, _, _) => new Size(10, 10),
            ImageTranslateBackgroundSample.Dark);

        Assert.Equal(Color.FromArgb(205, 0, 0, 0), plan.OverlayBackgroundColor);
        Assert.Equal(Colors.White, plan.ForegroundColor);
    }

    [Fact]
    public void NeutralBackgroundChoosesHigherContrastOverlay()
    {
        var block = Block(
            Box(0, 0, 200, 40),
            Box(0, 0, 180, 20));

        var plan = ImageTranslateTextOverlayLayout.Create(
            block,
            new Rect(0, 0, 200, 40),
            (_, _, _) => new Size(10, 10),
            ImageTranslateBackgroundSample.Neutral);

        Assert.Equal(Color.FromArgb(210, 255, 255, 255), plan.OverlayBackgroundColor);
        Assert.Equal(Colors.Black, plan.ForegroundColor);
    }

    [Fact]
    public void OverlayRectCoversEraseRectsAndTextClip()
    {
        var block = Block(
            Box(10, 20, 180, 46),
            Box(10, 20, 180, 20),
            Box(10, 46, 180, 20));

        var plan = CreatePlan(block, new Rect(10, 20, 180, 46), (_, _) => new Size(10, 10));

        AssertCovers(plan.OverlayRect, plan.TextClipRect);
        Assert.All(plan.EraseRects, eraseRect => AssertCovers(plan.OverlayRect, eraseRect));
    }

    [Theory]
    [InlineData("\r\n为快速开始，请选择以下任一安装方法：\r\n", "为快速开始，请选择以下任一安装方法：")]
    [InlineData("前往PowerToys的GitHub版本页面，\n  向下滚动并选择 Assets", "前往PowerToys的GitHub版本页面，向下滚动并选择Assets")]
    [InlineData("hello\nworld", "hello world")]
    [InlineData("  \t\n  ", "")]
    public void NormalizeOverlayTextRemovesLeadingLineBreaksAndCollapsesWhitespace(string input, string expected)
    {
        var result = ImageTranslateTextOverlayLayout.NormalizeOverlayText(input);

        Assert.Equal(expected, result);
    }

    private static ImageTranslateTextOverlayPlan CreatePlan(
        OcrLayoutBlock block,
        Rect boundingRect,
        Func<double, Rect, Size> measureText) =>
        ImageTranslateTextOverlayLayout.Create(
            block,
            boundingRect,
            (fontSize, textRect, _) => measureText(fontSize, textRect),
            ImageTranslateBackgroundSample.Light);

    private static void AssertCovers(Rect outer, Rect inner)
    {
        Assert.True(outer.Left <= inner.Left);
        Assert.True(outer.Top <= inner.Top);
        Assert.True(outer.Right >= inner.Right);
        Assert.True(outer.Bottom >= inner.Bottom);
    }

    private static OcrLayoutBlock Block(List<BoxPoint> boxPoints, params List<BoxPoint>[] lineBoxPoints) =>
        new()
        {
            Text = "translated text",
            BoxPoints = boxPoints,
            LineBoxPoints = [.. lineBoxPoints]
        };

    private static List<BoxPoint> Box(double left, double top, double width, double height) =>
    [
        new((float)left, (float)top),
        new((float)(left + width), (float)top),
        new((float)(left + width), (float)(top + height)),
        new((float)left, (float)(top + height))
    ];
}
