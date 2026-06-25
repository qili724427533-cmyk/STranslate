using STranslate.Plugin;
using System.Windows;
using System.Windows.Media;

namespace STranslate.Core;

internal sealed record ImageTranslateTextOverlayPlan(
    Rect BoundingRect,
    Rect TextRect,
    Rect TextClipRect,
    Rect OverlayRect,
    IReadOnlyList<Rect> EraseRects,
    double FontSize,
    double LineHeight,
    double MaxTextHeight,
    int MaxLineCount,
    bool IsMultiLine,
    bool ShouldTrim,
    Color ForegroundColor,
    Color OverlayBackgroundColor,
    double CornerRadius)
{
    internal const double MinFontSize = 6;
    internal const double MaxFontSize = 128;

    /// <summary>多行渲染时行高相对字号的倍数（行高 = 字号 × 此倍数）。</summary>
    internal const double MultilineLineHeightScale = 1.28;
}

internal enum ImageTranslateOverlayTheme
{
    Light,
    Dark
}

internal static class ImageTranslateTextOverlayLayout
{
    private const double MultilineFontScale = 0.90;
    private const double SingleLineFontScale = 1.08;
    private const double HorizontalTextPadding = 1;
    private const double ExpandedSingleLineMaxLines = 3.2;
    private const double ExpandedSingleLineVerticalPaddingScale = 0.12;
    private static readonly Color DarkOverlayBackground = Color.FromArgb(230, 0, 0, 0);
    private static readonly Color LightOverlayBackground = Color.FromArgb(235, 255, 255, 255);

    internal static ImageTranslateTextOverlayPlan Create(
        OcrLayoutBlock block,
        Rect boundingRect,
        Func<double, Rect, bool, Size> measureText,
        ImageTranslateOverlayTheme overlayTheme = ImageTranslateOverlayTheme.Light)
    {
        var lineRects = block.LineBoxPoints
            .Select(BoxPointLayout.BoundingRect)
            .Where(rect => !rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
            .ToList();

        var lineHeight = lineRects.Count > 0
            ? Median(lineRects.Select(rect => rect.Height))
            : Math.Max(1, boundingRect.Height);
        var isMultiLine = lineRects.Count > 1;
        var textVerticalPadding = isMultiLine
            ? Math.Max(1, lineHeight * 0.03)
            : 0;
        var textRect = CreatePaddedRect(
            boundingRect,
            HorizontalTextPadding,
            textVerticalPadding);
        var eraseRects = CreateEraseRects(lineRects, boundingRect, lineHeight);
        var textClipRect = CreateTextClipRect(boundingRect, eraseRects);

        var renderAsMultiLine = isMultiLine;
        var fitRect = isMultiLine
            ? textRect
            : new Rect(textRect.Left, textClipRect.Top, textRect.Width, textClipRect.Height);
        var originalLineLimit = lineHeight * (isMultiLine ? MultilineFontScale : SingleLineFontScale);
        var fontSizeLimit = CreateRegionFillFontSizeLimit(originalLineLimit, fitRect);
        var (fontSize, shouldTrim) = FitFontSize(fontSizeLimit, fitRect, isMultiLine, measureText);

        if (!isMultiLine && shouldTrim)
        {
            renderAsMultiLine = true;
            textClipRect = CreateExpandedSingleLineTextClipRect(textClipRect, lineHeight);
            textRect = CreatePaddedRect(
                textClipRect,
                HorizontalTextPadding,
                Math.Max(1, lineHeight * ExpandedSingleLineVerticalPaddingScale));
            fitRect = textRect;
            fontSizeLimit = Math.Clamp(
                originalLineLimit,
                ImageTranslateTextOverlayPlan.MinFontSize,
                ImageTranslateTextOverlayPlan.MaxFontSize);
            (fontSize, shouldTrim) = FitFontSize(fontSizeLimit, fitRect, true, measureText);
        }

        var overlayRect = textClipRect;
        var (overlayBackgroundColor, foregroundColor) = SelectOverlayColors(overlayTheme);
        var cornerRadius = Math.Clamp(lineHeight * 0.18, 3, 8);

        return new ImageTranslateTextOverlayPlan(
            boundingRect,
            textRect,
            textClipRect,
            overlayRect,
            eraseRects,
            fontSize,
            renderAsMultiLine ? fontSize * ImageTranslateTextOverlayPlan.MultilineLineHeightScale : 0,
            renderAsMultiLine ? textRect.Height : double.PositiveInfinity,
            renderAsMultiLine ? 0 : 1,
            renderAsMultiLine,
            shouldTrim,
            foregroundColor,
            overlayBackgroundColor,
            cornerRadius);
    }

    internal static string NormalizeOverlayText(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                if (ShouldKeepCollapsedSpace(builder[^1], ch))
                    builder.Append(' ');

                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static bool ShouldKeepCollapsedSpace(char previous, char current)
    {
        if (TextHelper.IsCjk(previous) || TextHelper.IsCjk(current))
            return false;

        if (char.IsPunctuation(current))
            return false;

        if (char.IsPunctuation(previous))
            return previous is ',' or ';' or ':';

        return true;
    }

    private static (double FontSize, bool ShouldTrim) FitFontSize(
        double fontSizeLimit,
        Rect textRect,
        bool isMultiLine,
        Func<double, Rect, bool, Size> measureText)
    {
        var minFontSize = ImageTranslateTextOverlayPlan.MinFontSize;
        if (!Fits(measureText(minFontSize, textRect, isMultiLine), textRect))
            return (minFontSize, true);

        if (Fits(measureText(fontSizeLimit, textRect, isMultiLine), textRect))
            return (fontSizeLimit, false);

        var low = minFontSize;
        var high = fontSizeLimit;
        var best = minFontSize;
        while (high - low > 0.5)
        {
            var mid = (low + high) / 2;
            if (Fits(measureText(mid, textRect, isMultiLine), textRect))
            {
                best = mid;
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (best, false);
    }

    private static double CreateRegionFillFontSizeLimit(double originalLineLimit, Rect fitRect) =>
        Math.Clamp(
            Math.Max(originalLineLimit, fitRect.Height * 1.2),
            ImageTranslateTextOverlayPlan.MinFontSize,
            ImageTranslateTextOverlayPlan.MaxFontSize);

    private static bool Fits(Size measured, Rect textRect) =>
        measured.Width <= textRect.Width + 0.1 &&
        measured.Height <= textRect.Height + 0.1;

    private static (Color Background, Color Foreground) SelectOverlayColors(ImageTranslateOverlayTheme overlayTheme) =>
        overlayTheme == ImageTranslateOverlayTheme.Dark
            ? (DarkOverlayBackground, Colors.White)
            : (LightOverlayBackground, Colors.Black);

    private static List<Rect> CreateEraseRects(IReadOnlyList<Rect> lineRects, Rect boundingRect, double lineHeight)
    {
        if (lineRects.Count == 0)
            return [boundingRect];

        var horizontalPadding = Math.Max(2, lineHeight * 0.08);
        var verticalPadding = Math.Max(2, lineHeight * 0.18);
        return lineRects
            .Select(rect => ExpandRect(rect, horizontalPadding, verticalPadding))
            .ToList();
    }

    private static Rect CreateTextClipRect(Rect boundingRect, IReadOnlyList<Rect> eraseRects)
    {
        var textClipRect = boundingRect;
        foreach (var eraseRect in eraseRects)
            textClipRect.Union(eraseRect);

        return textClipRect;
    }

    private static Rect CreateExpandedSingleLineTextClipRect(Rect textClipRect, double lineHeight)
    {
        var targetHeight = Math.Max(textClipRect.Height, lineHeight * ExpandedSingleLineMaxLines);
        var top = textClipRect.Top - (targetHeight - textClipRect.Height) / 2;
        return new Rect(textClipRect.Left, top, textClipRect.Width, targetHeight);
    }

    private static Rect ExpandRect(Rect rect, double horizontalPadding, double verticalPadding) =>
        new(
            rect.Left - horizontalPadding,
            rect.Top - verticalPadding,
            rect.Width + horizontalPadding * 2,
            rect.Height + verticalPadding * 2);

    private static Rect CreatePaddedRect(Rect rect, double horizontalPadding, double verticalPadding)
    {
        var width = Math.Max(1, rect.Width - horizontalPadding * 2);
        var height = Math.Max(1, rect.Height - verticalPadding * 2);
        return new Rect(rect.Left + horizontalPadding, rect.Top + verticalPadding, width, height);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Where(x => x > 0).Order().ToList();
        if (ordered.Count == 0)
            return 1;

        var middle = ordered.Count / 2;
        return ordered.Count % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }
}
