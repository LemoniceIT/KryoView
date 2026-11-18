using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class Program : Form
{
    private static readonly Color Background = Color.FromArgb(24, 26, 34);
    private static readonly Color Accent = Color.FromArgb(164, 192, 132);
    private static readonly string[] Orientations =
        { "Landscape", "Portrait", "Landscape (flipped)", "Portrait (flipped)" };
    private readonly NotifyIcon tray;
    private int orientation;
    private SensorTheme sensorTheme = SensorTheme.Classic;
    private ImportedSkin importedSkin;
    private string importedLayout;
    private static readonly string ImportedThemes = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KryoView", "themes");
    private SensorTheme lastTheme;
    private bool lastFrameWasImported;
    private readonly CheckBox hidePackets = new CheckBox { Text = "Hide USB packet transfers", AutoSize = true, Checked = true };
    private readonly System.Collections.Generic.List<Tuple<string, bool>> logEntries =
        new System.Collections.Generic.List<Tuple<string, bool>>();
    private readonly Button sensors;
    private readonly Button desktop;
    private readonly TextBox log = new TextBox
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        BackColor = Color.FromArgb(15, 17, 23),
        ForeColor = Color.WhiteSmoke
    };
    private string lastStatus;
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
    private readonly System.Windows.Forms.Timer videoTimer = new System.Windows.Forms.Timer { Interval = 30 };
    private Panel panel;
    private VideoLoop video;
    private string videoOverride;
    private string activeVideo;
    private string panelId;
    private bool desktopConnected;
    private bool enabled;
    private bool busy;
    private bool exiting;
    private SensorSampler sampler;
    private string[] lastReadings;
    private int lastAngle;
    private DateTime nextProbe;
    private DateTime nextTransfer;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test")
        {
            byte[] packet = Panel.Packet(10, null, 0x01020304);
            if (BitConverter.ToString(packet, 0, 16) != "CC-FF-A6-FB-53-02-10-74-FD-8E-C7-12-B3-92-C6-A6" ||
                BitConverter.ToString(packet, 496, 16) != "EC-54-35-A2-83-9F-C6-A3-00-00-00-00-00-00-A1-1A")
                Environment.Exit(1);
            foreach (SensorTheme theme in SensorTheme.Load(message => Environment.Exit(4)))
            for (int angle = 0; angle < 4; angle++)
            {
                using (Bitmap frame = SensorSkin.Draw(new[]
                {
                    "42%", "31%", "65%", "16", "10.4 GiB", "5.6 GiB", "402.1 GiB",
                    "124 KiB/s", "20 KiB/s", "01h 23m", "AC power"
                }, angle, theme))
                {
                    bool horizontal = angle % 2 == 0;
                    if (frame.Width != (horizontal ? 1920 : 480) ||
                        frame.Height != (horizontal ? 480 : 1920)) Environment.Exit(2);
                    SensorSkin.RotateForPanel(frame, angle);
                    if (frame.Width != 480 || frame.Height != 1920) Environment.Exit(2);
                    using (var buffer = new MemoryStream())
                    {
                        SensorSkin.SaveJpeg(frame, buffer);
                        if (buffer.Length == 0 || buffer.Length > 1048576) Environment.Exit(2);
                        buffer.Position = 0;
                        using (var decoded = Image.FromStream(buffer, false, true))
                            if (decoded.RawFormat.Guid != ImageFormat.Jpeg.Guid ||
                                decoded.Width != 480 || decoded.Height != 1920) Environment.Exit(2);
                    }
                }
            }
            try { Panel.Detect(); }
            catch { Environment.Exit(3); }
            return;
        }
        bool first;
        using (var mutex = new Mutex(true, @"Local\KryoView", out first))
        {
            if (!first) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Program());
        }
    }

    private Program()
    {
        Text = "KryoView " + Application.ProductVersion;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        BackColor = Background;
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Consolas", 11);
        MinimumSize = new Size(750, 350);
        ClientSize = new Size(780, 440);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        Controls.Add(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "> KryoView", ForeColor = Accent, Font = new Font("Consolas", 20, FontStyle.Bold), AutoSize = true });


        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        sensors = AddButton(actions, "Sensors", async delegate
        {
            if (busy) return;
            if (desktopConnected && panelId == null)
            {
                await ReturnToSensors();
                return;
            }
            enabled = !enabled;
            Log(enabled ? "Sensors requested." : "Sensors stopped.");
            if (!enabled)
            {
                busy = true;
                try { await Task.Run(() => ClosePanel()); }
                finally { busy = false; }
                Log("USB session closed after stopping sensor updates.");
            }
            sensors.Text = enabled ? "Stop sensors" : "Sensors";
            nextProbe = DateTime.MinValue;
            nextTransfer = DateTime.MinValue;
            await UpdatePanel();
        });
        desktop = AddButton(actions, "USB monitor", EnableDesktop);
        using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\KryoView"))
        {
            if (settings != null)
            {
                orientation = Math.Max(0, Math.Min(3, (settings.GetValue("Orientation", 0) as int?) ?? 0));
                hidePackets.Checked = (settings.GetValue("HidePackets", 1) as int?) != 0;
                string id = settings.GetValue("SensorTheme", "") as string;
                sensorTheme = SensorTheme.Load(Log).FirstOrDefault(theme =>
                    string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? SensorTheme.Classic;
                string importedId = settings.GetValue("ImportedTheme", "") as string;
                Guid importedGuid;
                if (Guid.TryParseExact(importedId, "N", out importedGuid))
                    TrySelectImported(Path.Combine(ImportedThemes, importedGuid.ToString("N"), "layout.xml"), false);
            }
        }

        AddButton(actions, "Options", delegate(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            var orientationMenu = new ToolStripMenuItem("Orientation");
            menu.Items.Add(orientationMenu);
            for (int i = 0; i < Orientations.Length; i++)
            {
                int angle = i;
                var item = new ToolStripMenuItem(Orientations[i]) { Checked = orientation == i };
                item.Click += async delegate
                {
                    orientation = angle;
                    SaveSetting("Orientation", angle);
                    await UpdatePanel();
                };
                orientationMenu.DropDownItems.Add(item);
            }
            ShowMenu(menu, (Control)sender);
        });

        AddButton(actions, "Sensor themes", delegate(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            foreach (SensorTheme theme in SensorTheme.Load(Log))
            {
                var item = new ToolStripMenuItem(theme.Name)
                {
                    Checked = importedSkin == null && string.Equals(sensorTheme.Id, theme.Id, StringComparison.OrdinalIgnoreCase)
                };
                item.Click += async delegate
                {
                    sensorTheme = theme;
                    importedSkin = null;
                    importedLayout = null;
                    SaveSetting("ImportedTheme", "");
                    SaveSetting("SensorTheme", theme.Id);
                    Log("Sensor theme selected: " + theme.Name);
                    if (enabled) await UpdatePanel();
                };
                menu.Items.Add(item);
            }
            menu.Items.Add(new ToolStripSeparator());
            if (Directory.Exists(ImportedThemes))
            {
                foreach (string folder in Directory.GetDirectories(ImportedThemes))
                {
                    string path = Path.Combine(folder, "layout.xml");
                    if (!File.Exists(path)) continue;
                    try
                    {
                        var skin = new ImportedSkin(path);
                        var item = new ToolStripMenuItem(skin.Name) { Checked = importedLayout == path };
                        item.Click += async delegate
                        {
                            if (TrySelectImported(path, true) && enabled) await UpdatePanel();
                        };
                        menu.Items.Add(item);
                    }
                    catch (Exception error) { Log("Could not open imported theme. " + error.Message); }
                }
            }
            menu.Items.Add("Import TURZX theme…", null, ImportTheme);
            var edit = menu.Items.Add("Edit imported labels…", null, delegate
            {
                try { Process.Start("notepad.exe", "\"" + importedLayout + "\""); }
                catch (Exception error) { Log(error.Message); }
            });
            edit.Enabled = importedLayout != null;
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Choose video background…", null, ChooseVideo);
            var resetVideo = menu.Items.Add("Use theme background", null, async delegate
            {
                videoOverride = null;
                await UpdatePanel();
            });
            resetVideo.Enabled = videoOverride != null;
            ShowMenu(menu, (Control)sender);
        });

        hidePackets.CheckedChanged += delegate
        {
            SaveSetting("HidePackets", hidePackets.Checked ? 1 : 0);
            log.Lines = logEntries.Where(entry => !entry.Item2 || !hidePackets.Checked)
                .Select(entry => entry.Item1).ToArray();
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        };
        layout.Controls.Add(hidePackets);
        layout.Controls.Add(actions);
        layout.Controls.Add(log);
        SetStatus("Looking for the panel…");

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Open KryoView", null, delegate { Reopen(); });
        trayMenu.Items.Add("Install TURZX display driver…", null, InstallDesktopDriver);
        trayMenu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
        tray = new NotifyIcon { Text = "KryoView", Icon = Icon, ContextMenuStrip = trayMenu, Visible = true };
        tray.DoubleClick += delegate { Reopen(); };
        timer.Tick += async delegate { await UpdatePanel(); };
        videoTimer.Tick += async delegate { await AdvanceVideo(); };
        Shown += async delegate { timer.Start(); await UpdatePanel(); };
    }

    private static Button AddButton(Control parent, string caption, EventHandler click)
    {
        var button = new Button { Text = caption, Height = 34, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Accent };
        button.Click += click;
        parent.Controls.Add(button);
        return button;
    }

    private void Reopen()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        timer.Stop();
        videoTimer.Stop();
        if (!busy) ClosePanel();
        tray.Dispose();
        timer.Dispose();
        videoTimer.Dispose();
        base.OnFormClosing(e);
    }

    private async Task UpdatePanel()
    {
        if (busy) return;
        busy = true;
        try
        {
            bool probe = DateTime.UtcNow >= nextProbe;
            if (probe)
            {
                nextProbe = DateTime.UtcNow.AddSeconds(3);
                string[] ids = await Task.Run(() => Panel.Detect());
                desktopConnected = ids.Any(id => Panel.IsModel(id, Panel.DesktopModel));
                string[] candidates = ids.Where(id => Panel.IsModel(id, Panel.UsbModel)).ToArray();
                string detected = candidates.Length == 1 ? candidates[0] : null;
                if (panelId != detected)
                {
                    ClosePanel();
                    panelId = detected;
                    nextTransfer = DateTime.MinValue;
                }
                desktop.Enabled = panelId != null;
                SetStatus(candidates.Length > 1 ? "Multiple V1.x panels detected. Connect one panel to continue." :
                    panelId != null ? (panel == null ? "TURZX 8.8″ V1.x · USB" : "TURZX 8.8″ V1.x · Dashboard active") :
                    ids.Any(id => id.IndexOf("PID_AD", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "USB display detected. Check Windows Display settings." :
                    ids.Length > 0 ? "Possible Rev C panel detected. USB support is in development." : "No TURZX 8.8″ panel detected.");
            }
            if (!enabled) ClosePanel();
            if (enabled && panelId != null && DateTime.UtcNow >= nextTransfer)
            {
                int angle = orientation;
                SensorTheme theme = sensorTheme;
                ImportedSkin imported = importedSkin;
                string background = videoOverride ?? (imported == null ? theme.Video : null);
                await Task.Run(delegate
                {
                    if (panel == null) panel = new Panel(panelId, Log, LogPacket);
                    if (sampler == null)
                    {
                        panel.StartSensorDisplay();
                        sampler = new SensorSampler();
                    }
                    if (!string.Equals(background, activeVideo, StringComparison.OrdinalIgnoreCase))
                    {
                        CloseVideo();
                        if (background != null)
                        {
                            video = new VideoLoop(panel, background);
                            activeVideo = background;
                            Log("Video background started: " + Path.GetFileName(background));
                        }
                        lastReadings = null;
                    }
                    string[] readings = sampler.Sample();
                    if (imported == null && !lastFrameWasImported && lastReadings != null &&
                        angle == lastAngle && theme == lastTheme && readings.SequenceEqual(lastReadings)) return;

                    using (Bitmap bitmap = imported == null ? SensorSkin.Draw(readings, angle, theme, video != null) :
                        imported.Draw(readings, angle % 2 == 0, video != null))
                    using (var buffer = new MemoryStream())
                    {
                        SensorSkin.RotateForPanel(bitmap, angle);
                        if (video == null)
                        {
                            SensorSkin.SaveJpeg(bitmap, buffer);
                            panel.ShowFrame(buffer.ToArray());
                        }
                        else
                        {
                            bitmap.Save(buffer, ImageFormat.Png);
                            panel.ShowOverlay(buffer.ToArray());
                        }
                    }
                    lastReadings = readings;
                    lastAngle = angle;
                    lastTheme = theme;
                    lastFrameWasImported = imported != null;
                });
                SetStatus("TURZX 8.8″ V1.x · Dashboard active");
            }
        }
        catch (Exception error)
        {
            ClosePanel();
            nextTransfer = DateTime.UtcNow.AddSeconds(10);
            MessageStatus(error.Message);
        }
        finally
        {
            if (!IsDisposed) videoTimer.Enabled = video != null;
            busy = false;
        }
    }

    private async void ChooseVideo(object sender, EventArgs e)
    {
        using (var dialog = new OpenFileDialog
        {
            Title = "Select an H.264 video background",
            Filter = "Annex B H.264 video (*.h264)|*.h264",
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            videoOverride = dialog.FileName;
            Log("Video background selected: " + Path.GetFileName(videoOverride));
        }
        await UpdatePanel();
    }

    private bool TrySelectImported(string path, bool save)
    {
        try
        {
            importedSkin = new ImportedSkin(path);
            importedLayout = path;
            if ((orientation % 2 == 0) != importedSkin.Landscape)
                orientation = importedSkin.Landscape ? 0 : 1;
            if (save)
            {
                SaveSetting("ImportedTheme", Path.GetFileName(Path.GetDirectoryName(path)));
                SaveSetting("Orientation", orientation);
                Log("Imported theme selected: " + importedSkin.Name);
            }
            return true;
        }
        catch (Exception error)
        {
            Log("Could not load imported theme. " + error.Message);
            return false;
        }
    }

    private async void ImportTheme(object sender, EventArgs e)
    {
        string source;
        using (var dialog = new OpenFileDialog
        {
            Title = "Import a TURZX 8.8-inch theme",
            Filter = "TURZX themes (*.turtheme)|*.turtheme",
            RestoreDirectory = true
        })
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            source = dialog.FileName;
        }

        string destination = Path.Combine(ImportedThemes, Guid.NewGuid().ToString("N"));
        string script = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Import-Turzx.ps1");
        try
        {
            Log("Importing " + Path.GetFileName(source) + "…");
            using (var converter = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh.exe",
                Arguments = "-NoProfile -NonInteractive -File \"" + script + "\" -Source \"" +
                    source + "\" -Destination \"" + destination + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }))
            {
                Task<string> output = converter.StandardOutput.ReadToEndAsync();
                Task<string> errors = converter.StandardError.ReadToEndAsync();
                await Task.Run(() => converter.WaitForExit());
                string report = (await output).Trim();
                if (report.Length > 0) Log(report.Replace(Environment.NewLine, " | "));
                if (converter.ExitCode != 0)
                    throw new IOException((await errors).Trim());
            }
            if (TrySelectImported(Path.Combine(destination, "layout.xml"), true) && enabled)
                await UpdatePanel();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Log("Theme import requires PowerShell 7 (pwsh.exe) and a compatible .NET Desktop Runtime.");
        }
        catch (Exception error) { Log("Theme import failed. " + error.Message); }
    }

    private async Task AdvanceVideo()
    {
        if (busy || !enabled || video == null) return;
        busy = true;
        try
        {
            await Task.Run(delegate
            {
                try { video.Advance(panel); }
                catch { ClosePanel(); throw; }
            });
        }
        catch (Exception error)
        {
            nextTransfer = DateTime.UtcNow.AddSeconds(10);
            MessageStatus(error.Message);
        }
        finally
        {
            if (!IsDisposed) videoTimer.Enabled = video != null;
            busy = false;
        }
    }

    private void SetStatus(string message)
    {
        if (lastStatus == message) return;
        lastStatus = message;
        Log(message);
    }

    private static void SaveSetting(string name, object value)
    {
        using (var settings = Registry.CurrentUser.CreateSubKey(@"Software\KryoView"))
            settings.SetValue(name, value);
    }

    private static void ShowMenu(ContextMenuStrip menu, Control button)
    {
        // WinForms can close the menu before dispatching the selected item's Click.
        menu.Closed += delegate { button.BeginInvoke(new Action(menu.Dispose)); };
        menu.Show(button, new Point(0, button.Height));
    }

    private void Log(string message) { Log(message, false); }

    private void LogPacket(string message) { Log(message, true); }

    private void Log(string message, bool packet)
    {
        string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message;
        if (InvokeRequired)
        {
            if (!Disposing && !IsDisposed)
            {
                try { BeginInvoke(new Action(() => AppendLog(line, packet))); }
                catch (InvalidOperationException) { }
            }
            return;
        }
        AppendLog(line, packet);
    }

    private void AppendLog(string line, bool packet)
    {
        if (IsDisposed) return;
        if (logEntries.Count >= 2000) logEntries.RemoveRange(0, 200);
        logEntries.Add(Tuple.Create(line, packet));
        if (packet && hidePackets.Checked) return;
        line += Environment.NewLine;
        if (log.TextLength > 100000 && log.SelectionLength == 0)
        {
            int trim = log.Text.IndexOf('\n', 20000) + 1;
            log.Select(0, trim);
            log.SelectedText = "";
        }
        if (log.SelectionLength != 0)
        {
            int start = log.SelectionStart;
            int length = log.SelectionLength;
            log.AppendText(line);
            log.Select(start, length);
            return;
        }
        log.AppendText(line);
    }

    private void MessageStatus(string message)
    {
        Log(message);
        nextProbe = DateTime.UtcNow.AddSeconds(10);
    }

    private async void EnableDesktop(object sender, EventArgs e)
    {
        if (busy || panelId == null) return;
        Log("USB monitor requested.");
        busy = true;
        desktop.Enabled = false;
        string selectedPanel = panelId;
        try
        {
            SetStatus("Checking for the TURZX display driver…");
            if (!await Task.Run(() => DesktopDriver.IsStaged()))
            {
                MessageStatus("Driver missing. Select Install TURZX display driver in the notification area.");
                return;
            }
            SetStatus("TURZX display driver found.");
            if (MessageBox.Show(this,
                "The panel will restart in monitor mode. Select Sensors in KryoView to switch back.",
                "Experimental USB monitor mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            enabled = false;
            sensors.Text = "Sensors";
            SetStatus("Sending the mode switch to the panel…");
            string[] previous = await Task.Run(() => Panel.Detect());
            Task switching = Task.Run(delegate
            {
                try
                {
                    if (panel == null) panel = new Panel(selectedPanel, Log, LogPacket, synchronize: false);
                    CloseVideo();
                    panel.EnterDesktop();
                }
                finally { ClosePanel(); }
            });
            if (await Task.WhenAny(switching, Task.Delay(5000)) != switching)
                SetStatus("USB monitor switch still pending after 5000 ms.");
            await switching;

            SetStatus("Waiting for Windows to detect the panel in monitor mode…");
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(500);
                string[] ids = await Task.Run(() => Panel.Detect());
                if (ids.Except(previous, StringComparer.OrdinalIgnoreCase)
                    .Any(id => Panel.IsModel(id, Panel.DesktopModel)))
                {
                    desktopConnected = true;
                    panelId = null;
                    MessageStatus("USB monitor detected. Open Display settings and select Extend.");
                    return;
                }
            }
            MessageStatus("Windows did not detect the panel in monitor mode. Check the USB connection and display driver.");
        }
        catch (Exception error) { MessageStatus(error.Message); }
        finally
        {
            desktop.Enabled = panelId != null;
            busy = false;
        }
    }

    private async Task ReturnToSensors()
    {
        busy = true;
        try
        {
            SetStatus("Querying the TURZX panel model…");
            bool switched = await Task.Run(() => Panel.ExitDesktop(() => (bool)Invoke(new Func<bool>(() =>
                MessageBox.Show(this,
                    "The panel did not answer the model query. If the connected TURZX panel is an 8.8-inch model, select Yes to try switching it to Sensors.",
                    "Confirm TURZX panel model", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes))));
            if (!switched)
            {
                MessageStatus("Sensor mode switch cancelled. The panel remains in monitor mode.");
                return;
            }
            enabled = true;
            sensors.Text = "Stop sensors";
            desktopConnected = false;
            nextTransfer = DateTime.MinValue;
            nextProbe = DateTime.MinValue;
            SetStatus("Waiting for the panel to return to sensor mode…");
        }
        catch (Exception error) { MessageStatus(error.Message); }
        finally { busy = false; }
    }

    private async void InstallDesktopDriver(object sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        try
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "Select the INF from the original TURZX display driver package",
                Filter = "Windows driver package (*.inf)|*.inf",
                CheckFileExists = true,
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string inf = dialog.FileName;
                int result = await Task.Run(() => DesktopDriver.Install(inf));
                if (result != 0 && result != 3010)
                    throw new IOException("Windows returned code " + result + " while installing the driver.");
                string message = result == 3010 ? "Restart Windows to finish installing the driver." :
                    "Windows added the driver package. You can now try USB monitor mode.";
                MessageBox.Show(this, message, "TURZX display driver");
                nextProbe = DateTime.MinValue;
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "TURZX display driver"); }
        finally { busy = false; }
    }

    private void ClosePanel()
    {
        CloseVideo();
        if (sampler != null) sampler.Dispose();
        sampler = null;
        lastReadings = null;
        if (panel != null) panel.Dispose();
        panel = null;
    }

    private void CloseVideo()
    {
        if (video == null) return;
        video.Dispose();
        video = null;
        activeVideo = null;
        try { panel.StopVideo(); }
        catch (IOException error) { Log(error.Message); }
    }
}
