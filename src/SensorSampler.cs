using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class SensorSampler : IDisposable
{
    private const string Unavailable = "—";
    private readonly GpuCounter gpu = new GpuCounter();
    private ulong lastIdle;
    private ulong lastTotal;
    private long lastNetworkTime;
    private Dictionary<string, long[]> lastNetwork = new Dictionary<string, long[]>();

    internal string[] Sample()
    {
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
        bool hasMemory = GlobalMemoryStatusEx(ref memory);
        string disk = Unavailable;
        try
        {
            disk = Size(new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)).AvailableFreeSpace);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        string download, upload;
        ReadNetwork(out download, out upload);
        var power = SystemInformation.PowerStatus;
        BatteryChargeStatus battery = power.BatteryChargeStatus;
        string powerText = power.PowerLineStatus == PowerLineStatus.Online ? "AC power" : Unavailable;
        if (battery != BatteryChargeStatus.Unknown && (battery & BatteryChargeStatus.NoSystemBattery) == 0)
            powerText = Percent(power.BatteryLifePercent * 100);
        TimeSpan uptime = TimeSpan.FromMilliseconds(GetTickCount64());

        return new[]
        {
            ReadCpu(), gpu.Read(),
            hasMemory ? Percent(memory.Load) : Unavailable,
            Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture),
            hasMemory ? Size(memory.TotalPhys - (double)memory.AvailPhys) : Unavailable,
            hasMemory ? Size(memory.AvailPhys) : Unavailable,
            disk, download, upload,
            uptime.ToString(uptime.Days > 0 ? @"d'd 'hh'h'" : @"hh'h 'mm'm'", CultureInfo.InvariantCulture),
            powerText
        };
    }

    private string ReadCpu()
    {
        ulong idle, kernel, user;
        if (!GetSystemTimes(out idle, out kernel, out user)) return Unavailable;
        ulong total = kernel + user;
        string result = lastTotal == 0 || total <= lastTotal || idle < lastIdle ? Unavailable :
            Percent(100 * (1 - (double)(idle - lastIdle) / (total - lastTotal)));
        lastIdle = idle;
        lastTotal = total;
        return result;
    }

    private void ReadNetwork(out string download, out string upload)
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - lastNetworkTime) / (double)Stopwatch.Frequency;
        long received = 0, sent = 0;
        var current = new Dictionary<string, long[]>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;
                try
                {
                    var stats = adapter.GetIPStatistics();
                    var sample = new[] { stats.BytesReceived, stats.BytesSent };
                    current[adapter.Id] = sample;
                    long[] previous;
                    if (lastNetwork.TryGetValue(adapter.Id, out previous))
                    {
                        received += Math.Max(0, sample[0] - previous[0]);
                        sent += Math.Max(0, sample[1] - previous[1]);
                    }
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }

        bool ready = lastNetworkTime > 0 && seconds > 0 && current.Count > 0;
        download = ready ? Size(received / seconds) + "/s" : Unavailable;
        upload = ready ? Size(sent / seconds) + "/s" : Unavailable;
        lastNetwork = current;
        lastNetworkTime = now;
    }

    private static string Size(double bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return bytes.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string Percent(double value)
    {
        return Math.Max(0, Math.Min(100, value)).ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    public void Dispose() { gpu.Dispose(); }

    private sealed class GpuCounter : IDisposable
    {
        private IntPtr query;
        private IntPtr counter;

        internal GpuCounter()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return;
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                Dispose();
        }

        internal string Read()
        {
            if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return Unavailable;
            const uint format = 0x200;
            const uint moreData = 0x800007d2;
            uint size = 0, count;
            if (PdhGetFormattedCounterArrayW(counter, format, ref size, out count, IntPtr.Zero) != moreData)
                return Unavailable;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, format, ref size, out count, buffer) != 0)
                    return Unavailable;
                var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                int stride = Marshal.SizeOf(typeof(CounterItem));
                double busiest = 0;
                for (int i = 0; i < count; i++)
                {
                    var item = (CounterItem)Marshal.PtrToStructure(IntPtr.Add(buffer, stride * i), typeof(CounterItem));
                    if (item.Status > 1 || double.IsNaN(item.Value)) continue;
                    string name = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrEmpty(name)) continue;
                    int engine = name.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
                    if (engine < 0) continue;
                    string key = name.Substring(engine);
                    double total;
                    engines.TryGetValue(key, out total);
                    total += Math.Max(0, item.Value);
                    engines[key] = total;
                    busiest = Math.Max(busiest, total);
                }
                return engines.Count == 0 ? Unavailable : Percent(busiest);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public void Dispose()
        {
            if (query == IntPtr.Zero) return;
            PdhCloseQuery(query);
            query = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterItem
        {
            public IntPtr Name;
            public uint Status;
            public double Value;
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhOpenQueryW(string source, IntPtr data, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr data, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", ExactSpelling = true)]
        private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr buffer);
        [DllImport("pdh.dll")]
        private static extern uint PdhCloseQuery(IntPtr query);
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
    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();
}
