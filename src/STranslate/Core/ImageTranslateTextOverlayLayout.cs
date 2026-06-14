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
    internal const double MaxFontSize = 48;
}

internal readonly record struct ImageTranslateBackgroundSample(double AverageLuminance, double DarkPixelRatio)
{
    internal static readonly ImageTranslateBackgroundSample Light = new(0.92, 0.02);
    internal static readonly ImageTranslateBackgroundSample Dark = new(0.08, 0.95);
    internal static readonly ImageTranslateBackgroundSample Neutral = new(0.5, 0.45);
}

internal static class ImageTranslateTextOverlayLayout
{
    private const double MultilineFontScale = 0.90;
    private const double SingleLineFontScale = 1.08;
    private const double HorizontalTextPadding = 1;
    private static readonly Color DarkOverlayBackground = Color.FromArgb(205, 0, 0, 0);
    private static readonly Color LightOverlayBackground = Color.FromArgb(210, 255, 255, 255);

    internal static ImageTranslateTextOverlayPlan Create(
        OcrLayoutBlock block,
        Rect boundingRect,
        Func<double, Rect, bool, Size> measureText,
        ImageTranslateBackgroundSample? backgroundSample = null)
    {
        var lineRects = block.LineBoxPoints
            .Select(CalculateBoundingRect)
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

        var fontSizeLimit = Math.Clamp(
            lineHeight * (isMultiLine ? MultilineFontScale : SingleLineFontScale),
            ImageTranslateTextOverlayPlan.MinFontSize,
            ImageTranslateTextOverlayPlan.MaxFontSize);
        var fitRect = isMultiLine
            ? textRect
            : new Rect(textRect.Left, textClipRect.Top, textRect.Width, textClipRect.Height);
        var (fontSize, shouldTrim) = FitFontSize(fontSizeLimit, fitRect, isMultiLine, measureText);
        var overlayRect = textClipRect;
        var (overlayBackgroundColor, foregroundColor) = SelectOverlayColors(
            backgroundSample ?? ImageTranslateBackgroundSample.Light);
        var cornerRadius = Math.Clamp(lineHeight * 0.18, 3, 8);

        return new ImageTranslateTextOverlayPlan(
            boundingRect,
            textRect,
            textClipRect,
            overlayRect,
            eraseRects,
            fontSize,
            isMultiLine ? fontSize * 1.28 : 0,
            isMultiLine ? textRect.Height : double.PositiveInfinity,
            isMultiLine ? 0 : 1,
            isMultiLine,
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
        if (IsCjk(previous) || IsCjk(current))
            return false;

        if (char.IsPunctuation(current))
            return false;

        if (char.IsPunctuation(previous))
            return previous is ',' or ';' or ':';

        return true;
    }

    private static bool IsCjk(char ch) =>
        (ch >= '\u3400' && ch <= '\u9fff') ||
        (ch >= '\uf900' && ch <= '\ufaff') ||
        (ch >= '\u3040' && ch <= '\u30ff') ||
        (ch >= '\uac00' && ch <= '\ud7af');

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

    private static bool Fits(Size measured, Rect textRect) =>
        measured.Width <= textRect.Width + 0.1 &&
        measured.Height <= textRect.Height + 0.1;

    private static (Color Background, Color Foreground) SelectOverlayColors(ImageTranslateBackgroundSample sample)
    {
        if (sample.AverageLuminance >= 0.62 && sample.DarkPixelRatio < 0.55)
            return (LightOverlayBackground, Colors.Black);

        if (sample.AverageLuminance <= 0.32 || sample.DarkPixelRatio >= 0.68)
            return (DarkOverlayBackground, Colors.White);

        var darkOverlayLuminance = BlendLuminance(sample.AverageLuminance, 0, DarkOverlayBackground.A / 255d);
        var lightOverlayLuminance = BlendLuminance(sample.AverageLuminance, 1, LightOverlayBackground.A / 255d);
        var darkOverlayContrast = ContrastRatio(darkOverlayLuminance, 1);
        var lightOverlayContrast = ContrastRatio(lightOverlayLuminance, 0);

        return darkOverlayContrast >= lightOverlayContrast
            ? (DarkOverlayBackground, Colors.White)
            : (LightOverlayBackground, Colors.Black);
    }

    private static double BlendLuminance(double baseLuminance, double overlayLuminance, double overlayAlpha) =>
        overlayLuminance * overlayAlpha + baseLuminance * (1 - overlayAlpha);

    private static double ContrastRatio(double luminanceA, double luminanceB)
    {
        var lighter = Math.Max(luminanceA, luminanceB);
        var darker = Math.Min(luminanceA, luminanceB);
        return (lighter + 0.05) / (darker + 0.05);
    }

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

    private static Rect CalculateBoundingRect(IReadOnlyList<BoxPoint> boxPoints)
    {
        if (boxPoints.Count == 0)
            return Rect.Empty;

        var minX = boxPoints.Min(p => p.X);
        var minY = boxPoints.Min(p => p.Y);
        var maxX = boxPoints.Max(p => p.X);
        var maxY = boxPoints.Max(p => p.Y);
        return new Rect(minX, minY, maxX - minX, maxY - minY);
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
