using System;
using System.Drawing;
using System.IO;
using System.Linq;

internal sealed class SensorTheme
{
    internal readonly string Id, Name;
    internal readonly Color Background, Surface, Border, Text;
    internal readonly Color[] Accents;

    internal static readonly SensorTheme Classic = new SensorTheme("classic", new[]
    {
        "Classic", "#1B1E28", "#242939", "#454D65", "#E2E8F7", "#9DCF9C", "#84B4FA", "#E0AF68"
    });

    private SensorTheme(string id, string[] lines)
    {
        Id = id;
        Name = lines[0].Trim();
        var colors = lines.Skip(1).Select(line =>
        {
            string hex = line.Trim();
            if (hex.Length != 7 || hex[0] != '#') throw new FormatException("Expected #RRGGBB.");
            return Color.FromArgb(255, Color.FromArgb(Convert.ToInt32(hex.Substring(1), 16)));
        }).ToArray();
        Background = colors[0];
        Surface = colors[1];
        Border = colors[2];
        Text = colors[3];
        Accents = colors.Skip(4).ToArray();
    }

    internal static SensorTheme[] Load(Action<string> log)
    {
        var themes = new System.Collections.Generic.List<SensorTheme> { Classic };
        string directory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "themes");
        try
        {
            if (!Directory.Exists(directory)) return themes.ToArray();
            foreach (string path in Directory.GetFiles(directory, "*.theme").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (themes.Any(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    if (lines.Length != 8 || string.IsNullOrWhiteSpace(lines[0]))
                        throw new FormatException("Expected a theme name and seven colors.");
                    themes.Add(new SensorTheme(id, lines));
                }
                catch (IOException error) { log(id + ": " + error.Message); }
                catch (UnauthorizedAccessException error) { log(id + ": " + error.Message); }
                catch (FormatException error) { log(id + ": " + error.Message); }
            }
        }
        catch (IOException error) { log(error.Message); }
        catch (UnauthorizedAccessException error) { log(error.Message); }
        return themes.ToArray();
    }
}
