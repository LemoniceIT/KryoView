using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;

internal static class SensorSkin
{
    private static readonly string[] Labels =
    {
        "CPU", "GPU", "RAM", "CPU THREADS", "RAM USED", "RAM AVAILABLE",
        "SYSTEM DRIVE FREE", "NETWORK ↓", "NETWORK ↑", "UPTIME", "POWER"
    };
    private static readonly Color[] Accents =
    {
        Color.FromArgb(157, 207, 156), Color.FromArgb(132, 180, 250), Color.FromArgb(224, 175, 104)
    };

    internal static Bitmap Draw(string[] readings, int angle)
    {
        bool horizontal = angle % 2 == 0;
        var frame = new Bitmap(horizontal ? 1920 : 480, horizontal ? 480 : 1920, PixelFormat.Format24bppRgb);
        using (var canvas = Graphics.FromImage(frame))
        using (var background = new SolidBrush(Color.FromArgb(27, 30, 40)))
        using (var surface = new SolidBrush(Color.FromArgb(36, 41, 57)))
        using (var border = new Pen(Color.FromArgb(69, 77, 101), 2))
        using (var muted = new SolidBrush(Color.FromArgb(183, 195, 221)))
        using (var ink = new SolidBrush(Color.FromArgb(226, 232, 247)))
        using (var heading = new Font("Consolas", horizontal ? 35 : 42, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var label = new Font("Consolas", horizontal ? 19 : 23, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var primary = new Font("Consolas", horizontal ? 64 : 83, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var value = new Font("Consolas", horizontal ? 29 : 31, FontStyle.Bold, GraphicsUnit.Pixel))
        {
            canvas.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            canvas.FillRectangle(background, 0, 0, frame.Width, frame.Height);
            int margin = horizontal ? 38 : 24;
            canvas.DrawString("> KryoView", heading, ink, margin, horizontal ? 24 : 32);
            int divider = horizontal ? 78 : 106;
            canvas.DrawLine(border, margin, divider, frame.Width - margin, divider);

            for (int i = 0; i < 3; i++)
            {
                var card = horizontal
                    ? new Rectangle(margin + i * 620, 101, 604, 156)
                    : new Rectangle(margin, 137 + i * 271, 432, 251);
                using (var accent = new SolidBrush(Accents[i]))
                {
                    canvas.FillRectangle(surface, card);
                    canvas.DrawRectangle(border, card);
                    canvas.FillRectangle(accent, card.X, card.Y, horizontal ? 4 : 6, card.Height);
                    canvas.DrawString(Labels[i], label, accent, card.X + 21, card.Y + 12);
                    canvas.DrawString(readings[i], primary, ink, card.X + 18, card.Y + (horizontal ? 37 : 64));

                    var track = new Rectangle(card.X + 22, card.Bottom - (horizontal ? 23 : 39),
                        card.Width - 44, horizontal ? 5 : 9);
                    canvas.FillRectangle(background, track);
                    int percentage;
                    if (int.TryParse(readings[i].TrimEnd('%'), out percentage))
                        canvas.FillRectangle(accent, track.X, track.Y, track.Width * percentage / 100, track.Height);
                }
            }

            for (int i = 0; i < 8; i++)
            {
                var card = horizontal
                    ? new Rectangle(margin + (i % 4) * 465, 279 + (i / 4) * 86, 449, 74)
                    : new Rectangle(margin, 969 + i * 116, 432, 100);
                canvas.FillRectangle(surface, card);
                canvas.DrawRectangle(border, card);
                canvas.DrawString(Labels[i + 3], label, muted, card.X + 13, card.Y + (horizontal ? 5 : 10));
                canvas.DrawString(readings[i + 3], value, ink, card.X + 12, card.Y + (horizontal ? 28 : 40));
            }
        }
        return frame;
    }

    internal static void RotateForPanel(Bitmap frame, int angle)
    {
        var rotations = new[] { RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate180FlipNone,
            RotateFlipType.Rotate270FlipNone, RotateFlipType.RotateNoneFlipNone };
        frame.RotateFlip(rotations[angle]);
    }
}
