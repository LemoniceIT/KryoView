using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

internal sealed class Program : Form
{
    private static readonly Color Background = Color.FromArgb(24, 26, 34);
    private static readonly Color Accent = Color.FromArgb(164, 192, 132);
    private static readonly string[] Orientations =
        { "Orizzontale", "Verticale", "Orizzontale capovolto", "Verticale capovolto" };
    private readonly NotifyIcon tray;
    private readonly ComboBox orientation = new ComboBox();
    private readonly Button sensors;
    private readonly Button desktop;
    private readonly Label status = new Label();
    private readonly PictureBox preview = new PictureBox();
    private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
    private Panel panel;
    private string panelId;
    private bool desktopConnected;
    private bool enabled;
    private bool busy;
    private bool exiting;
    private bool refresh = true;
    private string lastMetrics;
    private DateTime nextProbe;
    private DateTime nextTransfer;
    private ulong lastIdle;
    private ulong lastTotal;

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
                using (Bitmap frame = DrawFrame("42%", "65%", angle))
                {
                    RotateFrame(frame, angle);
                    if (frame.Width != 480 || frame.Height != 1920) Environment.Exit(2);
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
        MinimumSize = new Size(670, 390);
        ClientSize = new Size(720, 425);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
        Controls.Add(layout);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.Controls.Add(new Label { Text = "> KryoView", ForeColor = Accent, Font = new Font("Consolas", 20, FontStyle.Bold), AutoSize = true });
        status.Dock = DockStyle.Fill;
        status.Text = "Ricerca del pannello…";
        layout.Controls.Add(status);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        sensors = AddButton(actions, "Sensori", async delegate
        {
            if (busy) return;
            if (desktopConnected && panelId == null)
            {
                await ReturnToSensors();
                return;
            }
            enabled = !enabled;
            sensors.Text = enabled ? "Ferma sensori" : "Sensori";
            refresh = true;
            nextProbe = DateTime.MinValue;
            nextTransfer = DateTime.MinValue;
            await UpdatePanel();
        });
        desktop = AddButton(actions, "Monitor USB", EnableDesktop);
        AddButton(actions, "Schermo Windows", delegate { Process.Start("ms-settings:display"); });
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
            refresh = true;
            await UpdatePanel();
        };
        actions.Controls.Add(orientation);
        layout.Controls.Add(actions);
        preview.Dock = DockStyle.Fill;
        preview.SizeMode = PictureBoxSizeMode.Zoom;
        layout.Controls.Add(preview);
        layout.Controls.Add(new Label { Text = "Anteprima sensori · Chiudi la finestra per restare in background", ForeColor = Accent, AutoSize = true });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Apri KryoView", null, delegate { Reopen(); });
        menu.Items.Add("Installa driver monitor TURZX…", null, InstallDesktopDriver);
        menu.Items.Add("Esci", null, delegate { exiting = true; Close(); });
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
        refresh = true;
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
        if (!busy && panel != null) panel.Dispose();
        tray.Dispose();
        timer.Dispose();
        if (preview.Image != null) preview.Image.Dispose();
        base.OnFormClosing(e);
    }

    private async Task UpdatePanel()
    {
        if (busy) return;
        busy = true;
        try
        {
            int angle = orientation.SelectedIndex;
            string cpu = null;
            string ram = null;
            bool send = false;
            if (Visible || (enabled && panelId != null && DateTime.UtcNow >= nextTransfer))
            {
                cpu = ReadCpu();
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
                ram = GlobalMemoryStatusEx(ref memory) ? memory.Load + "%" : "—";
                string metrics = cpu + "/" + ram;
                if (refresh || metrics != lastMetrics ||
                    (enabled && panelId != null && panel == null && DateTime.UtcNow >= nextTransfer))
                {
                    if (Visible)
                    {
                        Image previous = preview.Image;
                        preview.Image = DrawFrame(cpu, ram, angle);
                        if (previous != null) previous.Dispose();
                    }
                    send = enabled && panelId != null;
                    lastMetrics = metrics;
                    refresh = false;
                }
            }

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
                    refresh = true;
                    send = false;
                }
                desktop.Enabled = panelId != null;
                status.Text = candidates.Length > 1 ? "Più pannelli V1.x rilevati. Collegane uno per questo collaudo." :
                    panelId != null ? (panel == null ? "TURZX 8,8″ V1.x · USB" : "TURZX 8,8″ V1.x · Dashboard attiva") :
                    ids.Any(id => id.IndexOf("PID_AD", StringComparison.OrdinalIgnoreCase) >= 0)
                        ? "Display USB in possibile modalità monitor. Verifica in Impostazioni schermo." :
                    ids.Length > 0 ? "Possibile Rev C rilevata. Trasporto USB in sviluppo." : "Nessun TURZX 8,8″ rilevato.";
            }
            if (!enabled) ClosePanel();
            if (enabled && panelId != null && send && DateTime.UtcNow >= nextTransfer)
            {
                await Task.Run(delegate
                {
                    if (panel == null) panel = new Panel(panelId);
                    using (Bitmap bitmap = DrawFrame(cpu, ram, angle))
                    using (var buffer = new MemoryStream())
                    {
                        RotateFrame(bitmap, angle);
                        bitmap.Save(buffer, ImageFormat.Png);
                        panel.ShowFrame(buffer.ToArray());
                    }
                });
                status.Text = "TURZX 8,8″ V1.x · Dashboard attiva";
            }
        }
        catch (Exception error)
        {
            ClosePanel();
            refresh = true;
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
        try
        {
            if (!DesktopDriver.IsStaged())
            {
                InstallDesktopDriver(sender, e);
                return;
            }
        }
        catch (Exception error)
        {
            MessageStatus(error.Message);
            return;
        }
        if (MessageBox.Show(this,
            "Il pannello si riavvierà in modalità monitor. Per tornare indietro premi Sensori in KryoView.",
            "Modalità monitor USB sperimentale", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        if (busy || panelId == null) return;
        busy = true;
        enabled = false;
        sensors.Text = "Sensori";
        try
        {
            await Task.Run(delegate
            {
                if (panel == null) panel = new Panel(panelId);
                panel.EnterDesktop();
            });
            status.Text = "Pannello in riavvio. Apri Schermo Windows e prova Estendi.";
            nextProbe = DateTime.UtcNow.AddSeconds(4);
        }
        catch (Exception error) { MessageStatus(error.Message); }
        finally { ClosePanel(); busy = false; }
    }

    private async Task ReturnToSensors()
    {
        busy = true;
        try
        {
            await Task.Run(() => Panel.ExitDesktop());
            enabled = true;
            sensors.Text = "Ferma sensori";
            desktopConnected = false;
            refresh = true;
            nextTransfer = DateTime.MinValue;
            nextProbe = DateTime.MinValue;
            status.Text = "Attendo il pannello in modalità sensori…";
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
                Title = "Seleziona l’INF dal pacchetto originale del driver monitor TURZX",
                Filter = "Pacchetto driver Windows (*.inf)|*.inf",
                CheckFileExists = true,
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string inf = dialog.FileName;
                int result = await Task.Run(() => DesktopDriver.Install(inf));
                if (result != 0 && result != 3010)
                    throw new IOException("Windows ha restituito il codice " + result + " durante l’installazione del driver.");
                string message = result == 3010 ? "Riavvia Windows per completare l’installazione." :
                    "Pacchetto acquisito da Windows. Ora puoi provare Monitor USB.";
                MessageBox.Show(this, message, "Driver monitor TURZX");
                nextProbe = DateTime.MinValue;
            }
        }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Driver monitor TURZX"); }
        finally { busy = false; }
    }

    private void ClosePanel()
    {
        if (panel == null) return;
        panel.Dispose();
        panel = null;
    }

    private string ReadCpu()
    {
        ulong idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return "—";
        ulong total = kernel + user;
        string result = lastTotal == 0 || total <= lastTotal ? "—" :
            (Math.Max(0, 1 - (double)(idle - lastIdle) / (total - lastTotal)) * 100).ToString("0") + "%";
        lastIdle = idle;
        lastTotal = total;
        return result;
    }

    private static Bitmap DrawFrame(string cpu, string ram, int angle)
    {
        bool landscape = angle % 2 == 0;
        var bitmap = new Bitmap(landscape ? 1920 : 480, landscape ? 480 : 1920);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var heading = new Font("Consolas", 36, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var value = new Font("Consolas", 92, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var accent = new SolidBrush(Accent))
        using (var outline = new Pen(Color.FromArgb(79, 85, 106), 2))
        {
            graphics.Clear(Background);
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString("> KryoView", heading, accent, 40, 46);
            int cardWidth = landscape ? 875 : 400;
            int ramX = landscape ? 970 : 40;
            int ramY = landscape ? 125 : 610;
            graphics.DrawRectangle(outline, 40, 125, cardWidth, 260);
            graphics.DrawRectangle(outline, ramX, ramY, cardWidth, 260);
            graphics.DrawString("CPU", heading, accent, 65, 155);
            graphics.DrawString(cpu, value, Brushes.WhiteSmoke, 62, 205);
            graphics.DrawString("RAM", heading, accent, ramX + 25, ramY + 30);
            graphics.DrawString(ram, value, Brushes.WhiteSmoke, ramX + 22, ramY + 80);
        }
        return bitmap;
    }

    private static void RotateFrame(Bitmap bitmap, int angle)
    {
        var rotations = new[] { RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate180FlipNone,
            RotateFlipType.Rotate270FlipNone, RotateFlipType.RotateNoneFlipNone };
        bitmap.RotateFlip(rotations[angle]);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
