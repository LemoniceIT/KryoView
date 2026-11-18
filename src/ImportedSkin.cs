using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Xml;

internal sealed class ImportedSkin
{
    private readonly string folder;
    private readonly XmlElement[] widgets;
    internal readonly string Name;
    internal readonly bool Landscape;
    private static readonly Lazy<string> Cpu = new Lazy<string>(() => HardwareName("Win32_Processor"));
    private static readonly Lazy<string> Gpu = new Lazy<string>(() => HardwareName("Win32_VideoController"));

    internal ImportedSkin(string path)
    {
        folder = Path.GetDirectoryName(path);
        var document = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(path, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 1000000
        }))
            document.Load(reader);
        var root = document.DocumentElement;
        int width = Number(root, "width", 0);
        int height = Number(root, "height", 0);
        if (root.Name != "theme" || !((width == 480 && height == 1920) || (width == 1920 && height == 480)))
            throw new InvalidDataException("Expected a 480 × 1920 or 1920 × 480 theme.");
        Landscape = width > height;
        Name = root.GetAttribute("name");
        widgets = root.SelectNodes("widget").Cast<XmlElement>().Take(256).ToArray();
    }

    internal Bitmap Draw(string[] readings, bool landscape, bool overlay)
    {
        var frame = new Bitmap(Landscape ? 1920 : 480, Landscape ? 480 : 1920,
            overlay ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        try
        {
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(overlay ? Color.Transparent : Color.Black);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                foreach (var item in widgets)
                    DrawWidget(graphics, item, readings, overlay);
            }
            if (landscape != Landscape)
                frame.RotateFlip(Landscape ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    private void DrawWidget(Graphics graphics, XmlElement item, string[] readings, bool overlay)
    {
        int x = Number(item, "x", 0), y = Number(item, "y", 0);
        string kind = item.GetAttribute("type");
        if (kind == "Image")
        {
            string asset = item.GetAttribute("file");
            if (asset != Path.GetFileName(asset) || Path.GetExtension(asset) != ".png") return;
            using (var image = Image.FromFile(Path.Combine(folder, asset)))
            {
                if (overlay && image.Width >= 480 && image.Height >= 480) return;
                float zoom;
                if (!float.TryParse(item.GetAttribute("zoom_rate"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out zoom) || !(zoom > 0 && zoom <= 8)) zoom = 1;
                graphics.DrawImage(image, x, y, image.Width * zoom, image.Height * zoom);
            }
            return;
        }

        string value = kind == "Text" ? item.GetAttribute("label") :
            Metric(item.GetAttribute("metric"), item.GetAttribute("source"), readings);
        Color foreground = ReadColor(item, "color", Color.White);
        if (kind == "Text" || kind == "Data")
        {
            if (item.GetAttribute("unit") == "False") value = value.TrimEnd('%');
            int size = Math.Max(6, Math.Min(180, Number(item, "size", 20)));
            using (var font = new Font(item.GetAttribute("font").Length == 0 ? "Segoe UI" :
                item.GetAttribute("font"), size, item.GetAttribute("bold") == "True" ?
                FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(foreground))
            using (var format = new StringFormat
            {
                Alignment = (StringAlignment)Math.Max(0, Math.Min(2, Number(item, "align", 0)))
            })
            {
                float left = Math.Max(0, x);
                float right = graphics.VisibleClipBounds.Width - 10;
                float available = format.Alignment == StringAlignment.Center ? Math.Min(left, right - left) * 2 :
                    format.Alignment == StringAlignment.Far ? left : right - left;
                float measured = graphics.MeasureString(value, font).Width;
                if (available > 0 && measured > available)
                {
                    using (var fitted = new Font(font.FontFamily, Math.Max(6, size * available / measured),
                        font.Style, GraphicsUnit.Pixel))
                        graphics.DrawString(value, fitted, brush, new PointF(x, y), format);
                }
                else graphics.DrawString(value, font, brush, new PointF(x, y), format);
            }
            return;
        }

        int percent;
        int.TryParse(value.TrimEnd('%'), out percent);
        float fraction = Math.Max(0, Math.Min(100, percent)) / 100f;
        Color track = ReadColor(item, "track", Color.FromArgb(50, 50, 55));
        if (kind == "StatuBar")
        {
            int width = Math.Max(1, Number(item, "width", 100));
            int height = Math.Max(1, Number(item, "height", 8));
            using (var filled = new SolidBrush(foreground))
            using (var empty = new SolidBrush(track))
            {
                graphics.FillRectangle(empty, x, y, width, height);
                graphics.FillRectangle(filled, x, y, width * fraction, height);
            }
        }
        else if (kind == "ArchBar")
        {
            int diameter = Math.Max(1, Number(item, "diameter", 100));
            int thickness = Math.Max(1, Number(item, "archWidth", 8));
            using (var filled = new Pen(foreground, thickness))
            using (var empty = new Pen(track, thickness))
            {
                graphics.DrawArc(empty, x, y, diameter, diameter, 135, 270);
                if (fraction > 0) graphics.DrawArc(filled, x, y, diameter, diameter, 135, 270 * fraction);
            }
        }
    }

    private static int Number(XmlElement item, string name, int fallback)
    {
        int number;
        return int.TryParse(item.GetAttribute(name), out number) ? number : fallback;
    }

    private static Color ReadColor(XmlElement item, string name, Color fallback)
    {
        int color;
        return int.TryParse(item.GetAttribute(name), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
            out color) ? Color.FromArgb(color) : fallback;
    }

    private static string Metric(string id, string source, string[] sample)
    {
        switch (id)
        {
            case "CPULOAD": return sample[0];
            case "GPULOAD": return sample[1];
            case "RAMLOAD": return sample[2];
            case "RAM": return sample[4];
            case "RAMVALID": return sample[5];
            case "DOWNDSPEED": return sample[7];
            case "UPSPEED": return sample[8];
            case "CPUMODEL": return Cpu.Value;
            case "GPUMODEL": return Gpu.Value;
            case "TIME": return DateTime.Now.ToString("HH:mm");
            case "DATE": return DateTime.Now.ToString("yyyy-MM-dd");
            case "DAY": return DateTime.Now.ToString("dddd", source == "Day_en" ?
                CultureInfo.InvariantCulture : CultureInfo.CurrentCulture);
            case "DRVLOAD": return DriveLoad(source);
            default: return "—";
        }
    }

    private static string DriveLoad(string source)
    {
        if (source.Length != 1 || !char.IsLetter(source[0])) return "—";
        try
        {
            var drive = new DriveInfo(source);
            if (drive.IsReady && drive.TotalSize > 0)
                return ((drive.TotalSize - drive.TotalFreeSpace) / (double)drive.TotalSize).ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "—";
    }

    private static string HardwareName(string type)
    {
        try
        {
            using (var search = new ManagementObjectSearcher("SELECT Name FROM " + type))
            using (var result = search.Get())
            {
                foreach (ManagementObject device in result)
                    using (device)
                    {
                        string name = device["Name"] as string;
                        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                    }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return "—";
    }
}
