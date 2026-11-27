using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using PhoneDesk.Operations;

namespace PhoneDesk.Services;

/// <summary>
/// Draws the call-flow diagram of an auto attendant as a PNG picture, from the same layout and
/// colors the SVG text is written from. Only the desktop app has a drawing surface, so the MCP
/// server writes the SVG alone.
/// </summary>
public static class CallFlowPictureRenderer
{
    private const double Scale = 2;

    public static byte[] RenderPng(CallFlowDiagram diagram)
    {
        ArgumentNullException.ThrowIfNull(diagram);
        var size = new PixelSize((int)Math.Ceiling(diagram.Width * Scale), (int)Math.Ceiling(diagram.Height * Scale));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.FillRectangle(Brushes.White, new Rect(0, 0, diagram.Width, diagram.Height));
            var linePen = new Pen(new SolidColorBrush(Color.Parse("#9aa0c8")), 1.5);
            foreach (var line in diagram.Lines)
            {
                var middle = (line.X1 + line.X2) / 2;
                var geometry = new StreamGeometry();
                using (var path = geometry.Open())
                {
                    path.BeginFigure(new Point(line.X1, line.Y1), false);
                    path.CubicBezierTo(new Point(middle, line.Y1), new Point(middle, line.Y2), new Point(line.X2, line.Y2));
                    path.EndFigure(false);
                }

                context.DrawGeometry(null, linePen, geometry);
            }

            foreach (var box in diagram.Boxes)
            {
                var (fill, stroke, ink) = RichReport.Colors(box.Kind);
                var rectangle = new Rect(box.X, box.Y, box.Width, box.Height);
                context.DrawRectangle(new SolidColorBrush(Color.Parse(fill)), new Pen(new SolidColorBrush(Color.Parse(stroke)), 1), rectangle, 7, 7);
                var brush = new SolidColorBrush(Color.Parse(ink));
                DrawText(context, box.Title, FontWeight.SemiBold, brush, box, 4);
                DrawText(context, box.Detail, FontWeight.Normal, brush, box, 18);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static void DrawText(DrawingContext context, string value, FontWeight weight, IBrush brush, CallFlowBox box, double top)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        var text = new FormattedText(
            value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, weight), 11, brush)
        {
            MaxTextWidth = Math.Max(10, box.Width - 20),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        context.DrawText(text, new Point(box.X + 10, box.Y + top));
    }
}
