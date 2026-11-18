using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

internal static class InstrumentSkin
{
    private static readonly string[] Primary = { "CPU LOAD", "GPU LOAD", "MEMORY" };
    private static readonly string[] Details =
    {
        "CPU THREADS", "MEMORY USED", "MEMORY FREE", "SYSTEM DRIVE FREE",
        "DOWNLOAD", "UPLOAD", "UPTIME", "POWER"
    };

    internal static Bitmap Draw(string[] readings, bool landscape, SensorTheme theme, bool overlay)
    {
        var frame = new Bitmap(landscape ? 1920 : 480, landscape ? 480 : 1920,
            overlay ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(frame))
        using (var ink = new SolidBrush(theme.Text))
        using (var label = FontFor(theme, landscape ? 19 : 23, FontStyle.Bold))
        using (var reading = FontFor(theme, landscape ? 25 : 32, FontStyle.Bold))
        using (var dial = FontFor(theme, landscape ? 49 : 72, FontStyle.Bold))
        {
            graphics.Clear(overlay ? Color.Transparent : theme.Background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            for (int i = 0; i < 3; i++)
            {
                Rectangle card = landscape
                    ? new Rectangle(24 + i * 628, 16, 616, 294)
                    : new Rectangle(24, 16 + i * 452, 432, 432);
                DrawCard(graphics, card, theme, overlay);
                using (var accent = new SolidBrush(theme.Accents[i]))
                    graphics.DrawString(Primary[i], label, accent, card.X + 17, card.Y + 13);

                int diameter = landscape ? 200 : 272;
                var gauge = new Rectangle(card.X + (card.Width - diameter) / 2,
                    card.Y + (landscape ? 52 : 81), diameter, diameter);
                DrawGauge(graphics, gauge, readings[i], i, theme, dial, ink);
            }

            for (int i = 0; i < Details.Length; i++)
            {
                Rectangle cell = landscape
                    ? new Rectangle(24 + i % 4 * 472, 327 + i / 4 * 70, 460, 62)
                    : new Rectangle(24 + i % 2 * 220, 1396 + i / 2 * 125, 212, 111);
                DrawCard(graphics, cell, theme, overlay);
                using (var detail = FontFor(theme, landscape ? 15 : 16, FontStyle.Bold))
                    graphics.DrawString(Details[i], detail, ink, cell.X + 12, cell.Y + 7);
                graphics.DrawString(readings[i + 3], reading, ink, cell.X + 11,
                    cell.Y + (landscape ? 25 : 43));
            }
        }
        return frame;
    }

    private static Font FontFor(SensorTheme theme, int size, FontStyle style)
    {
        return new Font(theme.Layout == SensorLayout.Touge ? "Consolas" : "Segoe UI",
            size, style, GraphicsUnit.Pixel);
    }

    private static void DrawCard(Graphics graphics, Rectangle card, SensorTheme theme, bool overlay)
    {
        using (var border = new Pen(theme.Border, 2))
        using (var surface = new SolidBrush(overlay ? Color.FromArgb(155, theme.Surface) : theme.Surface))
        {
            if (theme.Layout == SensorLayout.Clean)
            {
                using (var path = new GraphicsPath())
                {
                    const int corner = 24;
                    path.AddArc(card.X, card.Y, corner, corner, 180, 90);
                    path.AddArc(card.Right - corner, card.Y, corner, corner, 270, 90);
                    path.AddArc(card.Right - corner, card.Bottom - corner, corner, corner, 0, 90);
                    path.AddArc(card.X, card.Bottom - corner, corner, corner, 90, 90);
                    path.CloseFigure();
                    graphics.FillPath(surface, path);
                    graphics.DrawPath(border, path);
                }
            }
            else
            {
                graphics.FillRectangle(surface, card);
                graphics.DrawRectangle(border, card);
                if (theme.Layout == SensorLayout.Touge)
                {
                    using (var accent = new SolidBrush(theme.Accents[0]))
                        graphics.FillRectangle(accent, card.X, card.Y + 8, 4, card.Height - 16);
                }
            }
        }
    }

    private static void DrawGauge(Graphics graphics, Rectangle gauge, string value, int index,
        SensorTheme theme, Font font, Brush text)
    {
        int number;
        int.TryParse(value.TrimEnd('%'), out number);
        float fraction = Math.Max(0, Math.Min(100, number)) / 100f;
        const float start = 135;
        const float sweep = 270;
        using (var track = new Pen(theme.Border, gauge.Width * 0.065f))
        using (var active = new Pen(theme.Accents[index], track.Width))
        {
            track.StartCap = track.EndCap = LineCap.Round;
            active.StartCap = active.EndCap = LineCap.Round;
            graphics.DrawArc(track, gauge, start, sweep);
            if (fraction > 0)
            {
                if (theme.Layout == SensorLayout.Instrument)
                {
                    for (int step = 0; step < 36 && step / 36f < fraction; step++)
                    {
                        float progress = step / 36f;
                        active.Color = Blend(theme.Accents[(index + 1) % 3], theme.Accents[index], progress);
                        graphics.DrawArc(active, gauge, start + sweep * progress,
                            sweep * Math.Min(1 / 36f, fraction - progress));
                    }
                }
                else graphics.DrawArc(active, gauge, start, sweep * fraction);
            }
        }

        if (theme.Layout != SensorLayout.Clean)
        {
            float radius = gauge.Width * 0.40f;
            float x = gauge.Left + gauge.Width * 0.5f;
            float y = gauge.Top + gauge.Height * 0.5f;
            using (var tick = new Pen(theme.Border, 2))
            using (var redline = new Pen(theme.Accents[0], 2))
            {
                for (int i = 0; i <= 20; i++)
                {
                    double angle = (start + sweep * i / 20) * Math.PI / 180;
                    float dx = (float)Math.Cos(angle), dy = (float)Math.Sin(angle);
                    float inner = radius - (i % 5 == 0 ? 13 : 6);
                    graphics.DrawLine(theme.Layout == SensorLayout.Touge && i >= 16 ? redline : tick,
                        x + dx * radius, y + dy * radius, x + dx * inner, y + dy * inner);
                }
            }
        }

        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            graphics.DrawString(value, font, text, gauge, format);
    }

    private static Color Blend(Color start, Color end, float amount)
    {
        return Color.FromArgb(
            (int)(start.R + (end.R - start.R) * amount),
            (int)(start.G + (end.G - start.G) * amount),
            (int)(start.B + (end.B - start.B) * amount));
    }
}
