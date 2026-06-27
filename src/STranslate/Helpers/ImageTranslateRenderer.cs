using STranslate.Core;
using STranslate.Plugin;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace STranslate.Helpers;

/// <summary>
/// 图片翻译的译文覆盖图与 OCR 标注图渲染。
/// 从 <see cref="ViewModels.ImageTranslateWindowViewModel"/> 抽离的纯绘制逻辑，无 VM 状态依赖。
/// </summary>
internal static class ImageTranslateRenderer
{
    // 超采样（Super-sampling）参数：小图放大渲染以保证译文矢量绘制的清晰度。
    private const double SupersampleMinDimension = 1000;
    private const double SupersampleMaxScale = 4.0;
    private const double SupersampleMinScale = 2.0;

    /// <summary>
    /// 生成带有翻译文本覆盖的图像。
    /// </summary>
    /// <param name="layoutBlocks">包含翻译后文本的布局块</param>
    /// <param name="image">原始图像</param>
    /// <param name="overlayTheme">覆盖层主题（明/暗）</param>
    /// <returns>覆盖翻译文本后的图像及可选中的译文词集合</returns>
    internal static TranslatedImageRenderResult GenerateTranslatedImage(
        IReadOnlyList<OcrLayoutBlock> layoutBlocks,
        BitmapSource image,
        ImageTranslateOverlayTheme overlayTheme)
    {
        ArgumentNullException.ThrowIfNull(image);

        // 没有位置信息的话返回原图
        if (layoutBlocks.Count == 0 ||
            layoutBlocks.All(x => x.BoxPoints.Count == 0))
        {
            return new TranslatedImageRenderResult(image, []);
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

        // 最小边小于阈值时按比例放大，但限制在 [MinScale, MaxScale] 区间
        if (minDimension < SupersampleMinDimension)
        {
            scaleFactor = Math.Min(SupersampleMaxScale, SupersampleMinDimension / minDimension);
            // 确保至少放大 MinScale 倍以获得较好的抗锯齿效果
            scaleFactor = Math.Max(scaleFactor, SupersampleMinScale);
        }

        // 计算渲染目标尺寸
        int renderWidth = (int)(image.PixelWidth * scaleFactor);
        int renderHeight = (int)(image.PixelHeight * scaleFactor);

        var measureTextBrush = new SolidColorBrush(Colors.Black);
        measureTextBrush.Freeze();
        var overlays = layoutBlocks
            .Where(item => item.BoxPoints.Count > 0 && !string.IsNullOrEmpty(item.Text))
            .Select(item => CreateTranslatedTextOverlay(item, overlayTheme, pixelsPerDip, measureTextBrush))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();

        var selectableWords = OcrWordBuilder.CreateIndexedCollection(
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

        return new TranslatedImageRenderResult(renderBitmap, selectableWords);
    }

    /// <summary>
    /// 生成带有 OCR 识别边框标注的图像。
    /// </summary>
    /// <param name="ocrResult">OCR 识别结果</param>
    /// <param name="image">原始图像</param>
    /// <returns>标注边框后的图像；无位置信息则返回原图</returns>
    internal static BitmapSource GenerateAnnotatedImage(OcrResult ocrResult, BitmapSource image)
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

    /// <summary>
    /// 在指定区域绘制翻译文本覆盖层
    /// </summary>
    /// <param name="content">包含翻译文本和位置信息的内容</param>
    /// <param name="overlayTheme">覆盖层主题</param>
    /// <param name="pixelsPerDip">DPI缩放比例</param>
    /// <param name="measureTextBrush">测量文本用的画刷</param>
    private static TranslatedTextOverlay? CreateTranslatedTextOverlay(
        OcrLayoutBlock content,
        ImageTranslateOverlayTheme overlayTheme,
        double pixelsPerDip,
        Brush measureTextBrush)
    {
        var boundingRect = BoxPointLayout.BoundingRect(content.BoxPoints);
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
                isMultiLine ? fontSize * ImageTranslateTextOverlayPlan.MultilineLineHeightScale : 0,
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
                ? plan.TextRect.Top + Math.Max(0, (plan.TextRect.Height - formattedText.Height) / 2)
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

    private static Color CreateTextShadowColor(Color foregroundColor) =>
        IsLightColor(foregroundColor)
            ? Color.FromArgb(120, 0, 0, 0)
            : Color.FromArgb(120, 255, 255, 255);

    private static bool IsLightColor(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255d >= 0.5;

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
}

internal sealed record TranslatedImageRenderResult(
    BitmapSource Image,
    ObservableCollection<OcrWord> SelectableWords);
