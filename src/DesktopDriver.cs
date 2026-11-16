using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

internal static class DesktopDriver
{
    private static readonly Regex HardwareId = new Regex(
        @"^\s*[^;=]+=[^;]*,\s*USB\\VID_1A86&PID_AD11&MI_00\s*(?:[,;]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static bool Matches(string inf)
    {
        return HardwareId.IsMatch(File.ReadAllText(inf));
    }

    internal static bool IsStaged()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        return Directory.EnumerateFiles(directory, "oem*.inf").Any(path =>
        {
            try { return Matches(path); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        });
    }

    internal static int Install(string inf)
    {
        if (!Matches(inf))
            throw new InvalidDataException("Seleziona il file INF originale del driver desktop TURZX (1A86:AD11).");

        // Windows validates the signed package and performs device matching.
        using (var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
            Arguments = "/add-driver \"" + inf + "\" /install",
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }))
        {
            process.WaitForExit();
            return process.ExitCode;
        }
    }
}
