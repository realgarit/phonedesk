using PhoneDesk.Operations;
using PhoneDesk.Services.Interfaces;
using SkiaSharp;

namespace PhoneDesk.Services.Reports;

/// <summary>
/// Draws a call-flow diagram with SkiaSharp, the drawing library the desktop app already ships.
/// It needs no window or UI framework, so the MCP server draws the same picture as the desktop app.
/// </summary>
public sealed class SkiaCallFlowPictureRenderer : ICallFlowPictureRenderer
{
    private const float Scale = 2f;
    private const float FontSize = 11f;

    public byte[] RenderPng(CallFlowDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        var width = (int)Math.Ceiling(diagram.Width * Scale);
        var height = (int)Math.Ceiling(diagram.Height * Scale);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Scale(Scale);
        canvas.Clear(SKColors.White);

        using (var line = new SKPaint { Color = SKColor.Parse("#9aa0c8"), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke, IsAntialias = true })
        {
            foreach (var item in diagram.Lines)
            {
                var middle = (float)((item.X1 + item.X2) / 2);
                using var path = new SKPath();
                path.MoveTo((float)item.X1, (float)item.Y1);
                path.CubicTo(middle, (float)item.Y1, middle, (float)item.Y2, (float)item.X2, (float)item.Y2);
                canvas.DrawPath(path, line);
            }
        }

        using var regular = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal) ?? SKTypeface.Default;
        using var bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        foreach (var box in diagram.Boxes)
        {
            var (fill, stroke, ink) = RichReport.Colors(box.Kind);
            using var rectangle = new SKRoundRect(
                new SKRect((float)box.X, (float)box.Y, (float)(box.X + box.Width), (float)(box.Y + box.Height)), 7, 7);
            using (var fillPaint = new SKPaint { Color = SKColor.Parse(fill), Style = SKPaintStyle.Fill, IsAntialias = true })
            {
                canvas.DrawRoundRect(rectangle, fillPaint);
            }

            using (var strokePaint = new SKPaint { Color = SKColor.Parse(stroke), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true })
            {
                canvas.DrawRoundRect(rectangle, strokePaint);
            }

            DrawText(canvas, box.Title, bold, ink, box, 15);
            DrawText(canvas, box.Detail, regular, ink, box, 29);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void DrawText(SKCanvas canvas, string? value, SKTypeface typeface, string ink, CallFlowBox box, float baseline)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        using var paint = new SKPaint { Color = SKColor.Parse(ink), Typeface = typeface, TextSize = FontSize, IsAntialias = true };
        var room = (float)box.Width - 20;
        var text = value;
        if (paint.MeasureText(text) > room)
        {
            // Shortened to the width of its box, like the SVG.
            while (text.Length > 1 && paint.MeasureText(text + "…") > room)
            {
                text = text[..^1];
            }

            text += "…";
        }

        canvas.DrawText(text, (float)box.X + 10, (float)box.Y + baseline, paint);
    }
}
