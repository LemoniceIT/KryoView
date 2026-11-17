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
    private readonly ComboBox orientation = new ComboBox();
    private readonly Button sensors;
    private readonly Button desktop;
    private readonly Label status = new Label();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
    private Panel panel;
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
            for (int angle = 0; angle < 4; angle++)
            {
                using (Bitmap frame = SensorSkin.Draw(new[]
                {
                    "42%", "31%", "65%", "16", "10.4 GiB", "5.6 GiB", "402.1 GiB",
                    "124 KiB/s", "20 KiB/s", "01h 23m", "AC power"
                }, angle))
                {
                    bool horizontal = angle % 2 == 0;
                    if (frame.Width != (horizontal ? 1920 : 480) ||
                        frame.Height != (horizontal ? 480 : 1920)) Environment.Exit(2);
                    SensorSkin.RotateForPanel(frame, angle);
                    if (frame.Width != 480 || frame.Height != 1920) Environment.Exit(2);
                    using (var buffer = new MemoryStream())
                    {
                        frame.Save(buffer, ImageFormat.Png);
                        if (buffer.Length == 0 || buffer.Length > 1048576) Environment.Exit(2);
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
        Text = "KryoView";
        Icon = SystemIcons.Application;
        BackColor = Background;
        ForeColor = Color.WhiteSmoke;
        Font = new Font("Consolas", 11);
        MinimumSize = new Size(670, 235);
        ClientSize = new Size(720, 205);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        Controls.Add(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "> KryoView", ForeColor = Accent, Font = new Font("Consolas", 20, FontStyle.Bold), AutoSize = true });
        status.Dock = DockStyle.Fill;
        status.Text = "Looking for the panel…";
        layout.Controls.Add(status);

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
            sensors.Text = enabled ? "Stop sensors" : "Sensors";
            nextProbe = DateTime.MinValue;
            nextTransfer = DateTime.MinValue;
            await UpdatePanel();
        });
        desktop = AddButton(actions, "USB monitor", EnableDesktop);
        AddButton(actions, "Display settings", delegate { Process.Start("ms-settings:display"); });
        orientation.DropDownStyle = ComboBoxStyle.DropDownList;
        orientation.Width = 190;
        orientation.BackColor = Color.FromArgb(47, 50, 63);
        orientation.ForeColor = ForeColor;
        orientation.Margin = new Padding(7, 8, 0, 0);
        orientation.Items.AddRange(Orientations);
        using (var settings = Registry.CurrentUser.OpenSubKey(@"Software\KryoView"))
        {
            int saved = settings == null ? 0 : (settings.GetValue("Orientation", 0) as int? ?? 0);
            orientation.SelectedIndex = saved >= 0 && saved < Orientations.Length ? saved : 0;
        }
        orientation.SelectedIndexChanged += async delegate
        {
            using (var settings = Registry.CurrentUser.CreateSubKey(@"Software\KryoView"))
                settings.SetValue("Orientation", orientation.SelectedIndex);
            await UpdatePanel();
        };
        actions.Controls.Add(orientation);
        layout.Controls.Add(actions);
        layout.Controls.Add(new Label { Text = "Close the window to keep KryoView running in the background.", ForeColor = Accent, AutoSize = true });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open KryoView", null, delegate { Reopen(); });
        menu.Items.Add("Install TURZX display driver…", null, InstallDesktopDriver);
        menu.Items.Add("Exit", null, delegate { exiting = true; Close(); });
        tray = new NotifyIcon { Text = "KryoView", Icon = Icon, ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += delegate { Reopen(); };
        timer.Tick += async delegate { await UpdatePanel(); };
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
        if (!busy) ClosePanel();
        tray.Dispose();
        timer.Dispose();
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
                status.Text = candidates.Length > 1 ? "Multiple V1.x panels detected. Connect one panel to continue." :
                    panelId != null ? (panel == null ? "TURZX 8.8″ V1.x · USB" : "TURZX 8.8″ V1.x · Dashboard active") :
                    ids.Any(id => id.IndexOf("PID_AD", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "USB display detected. Check Windows Display settings." :
                    ids.Length > 0 ? "Possible Rev C panel detected. USB support is in development." : "No TURZX 8.8″ panel detected.";
            }
            if (!enabled) ClosePanel();
            if (enabled && panelId != null && DateTime.UtcNow >= nextTransfer)
            {
                int angle = orientation.SelectedIndex;
                await Task.Run(delegate
                {
                    if (panel == null) panel = new Panel(panelId);
                    if (sampler == null) sampler = new SensorSampler();
                    string[] readings = sampler.Sample();
                    if (lastReadings != null && angle == lastAngle && readings.SequenceEqual(lastReadings)) return;

                    using (Bitmap bitmap = SensorSkin.Draw(readings, angle))
                    using (var buffer = new MemoryStream())
                    {
                        SensorSkin.RotateForPanel(bitmap, angle);
                        bitmap.Save(buffer, ImageFormat.Png);
                        panel.ShowFrame(buffer.ToArray());
                    }
                    lastReadings = readings;
                    lastAngle = angle;
                });
                status.Text = "TURZX 8.8″ V1.x · Dashboard active";
            }
        }
        catch (Exception error)
        {
            ClosePanel();
            nextTransfer = DateTime.UtcNow.AddSeconds(10);
            MessageStatus(error.Message);
        }
        finally { busy = false; }
    }

    private void MessageStatus(string message)
    {
        status.Text = message;
        nextProbe = DateTime.UtcNow.AddSeconds(10);
    }

    private async void EnableDesktop(object sender, EventArgs e)
    {
        if (busy || panelId == null) return;
        busy = true;
        desktop.Enabled = false;
        string selectedPanel = panelId;
        try
        {
            status.Text = "Checking for the TURZX display driver…";
            if (!await Task.Run(() => DesktopDriver.IsStaged()))
            {
                MessageStatus("Driver missing. Select Install TURZX display driver in the notification area.");
                return;
            }
            status.Text = "TURZX display driver found.";
            if (MessageBox.Show(this,
                "The panel will restart in monitor mode. Select Sensors in KryoView to switch back.",
                "Experimental USB monitor mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            enabled = false;
            sensors.Text = "Sensors";
            status.Text = "Sending the mode switch to the panel…";
            string[] previous = await Task.Run(() => Panel.Detect());
            Task switching = Task.Run(delegate
            {
                try
                {
                    if (panel == null) panel = new Panel(selectedPanel);
                    panel.EnterDesktop();
                }
                finally { ClosePanel(); }
            });
            if (await Task.WhenAny(switching, Task.Delay(5000)) != switching)
                status.Text = "USB is taking longer than expected. If this persists, reconnect the panel.";
            await switching;

            status.Text = "Waiting for Windows to detect the panel in monitor mode…";
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
            await Task.Run(() => Panel.ExitDesktop());
            enabled = true;
            sensors.Text = "Stop sensors";
            desktopConnected = false;
            nextTransfer = DateTime.MinValue;
            nextProbe = DateTime.MinValue;
            status.Text = "Waiting for the panel to return to sensor mode…";
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
        if (sampler != null) sampler.Dispose();
        sampler = null;
        lastReadings = null;
        if (panel != null) panel.Dispose();
        panel = null;
    }
}
