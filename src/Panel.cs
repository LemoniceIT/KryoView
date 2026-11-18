using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed class Panel : IDisposable
{
    internal const string UsbModel = @"USB\VID_1CBE&PID_0088";
    internal const string DesktopModel = @"USB\VID_1A86&PID_AD11";
    private readonly SafeFileHandle file;
    private IntPtr usb;

    internal static string[] Detect()
    {
        uint size;
        if (CM_Get_Device_ID_List_SizeW(out size, "USB", 0x101) != 0)
            throw new IOException("Could not enumerate USB devices.");
        var list = new char[size];
        if (CM_Get_Device_ID_ListW("USB", list, size, 0x101) != 0)
            throw new IOException("The USB device list changed. Try again.");
        return new string(list).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(id => IsModel(id, UsbModel) ||
                         IsModel(id, @"USB\VID_0525&PID_A4A7") ||
                         IsModel(id, @"USB\VID_1A86&PID_CA88") ||
                         IsModel(id, @"USB\VID_1A86&PID_AD10") ||
                         IsModel(id, DesktopModel) ||
                         IsModel(id, @"USB\VID_1A86&PID_AD12") ||
                         IsModel(id, @"USB\VID_1A86&PID_AD13"))
            .ToArray();
    }

    internal static bool IsModel(string id, string model)
    {
        return id.StartsWith(model + "\\", StringComparison.OrdinalIgnoreCase);
    }

    internal Panel(string id)
    {
        var classes = new HashSet<Guid> { new Guid("a5dcbf10-6530-11d2-901f-00c04fb951ed") };
        using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + id + @"\Device Parameters"))
        {
            if (key != null)
            {
                foreach (string name in new[] { "DeviceInterfaceGUIDs", "DeviceInterfaceGUID" })
                {
                    object value = key.GetValue(name);
                    foreach (string entry in value as string[] ?? new[] { value as string })
                    {
                        Guid guid;
                        if (Guid.TryParse(entry, out guid)) classes.Add(guid);
                    }
                }
            }
        }
        foreach (Guid deviceClass in classes)
        {
            var guid = deviceClass;
            uint size;
            if (CM_Get_Device_Interface_List_SizeW(out size, ref guid, id, 0) != 0 || size <= 1)
                continue;
            var interfaces = new char[size];
            if (CM_Get_Device_Interface_ListW(ref guid, id, interfaces, size, 0) != 0)
                continue;
            foreach (string path in new string(interfaces).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = CreateFileW(path, 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
                if (!candidate.IsInvalid && WinUsb_Initialize(candidate, out usb))
                {
                    file = candidate;
                    try
                    {
                        uint timeout = 1250;
                        if (!WinUsb_SetPipePolicy(usb, 0x81, 3, 4, ref timeout) ||
                            !WinUsb_SetPipePolicy(usb, 0x01, 3, 4, ref timeout))
                            throw new Win32Exception();
                        if (Exchange(10, null)[0] != 10)
                            throw new IOException("Unexpected response from the panel.");
                        return;
                    }
                    catch
                    {
                        Dispose();
                        throw;
                    }
                }
                candidate.Dispose();
            }
        }
        throw new IOException("Cannot access the WinUSB interface. Close the TURZX app and check the USB driver.");
    }

    internal void ShowFrame(byte[] png)
    {
        if (png.Length > 1048576) throw new IOException("The image is too large for the panel.");
        if (Exchange(102, png)[0] != 102) throw new IOException("The panel did not acknowledge the frame.");
    }

    internal void EnterDesktop()
    {
        // The protocol does not define an acknowledgement for the mode command.
        Write(Packet(150, null, (int)DateTime.Now.TimeOfDay.TotalMilliseconds));
        Write(Packet(11, null, (int)DateTime.Now.TimeOfDay.TotalMilliseconds));
    }

    internal static bool ExitDesktop(Func<bool> confirmUnidentifiedPanel)
    {
        var hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        uint size;
        if (CM_Get_Device_Interface_List_SizeW(out size, ref hid, null, 0) != 0)
            throw new IOException("Could not enumerate HID interfaces.");
        var interfaces = new char[size];
        if (CM_Get_Device_Interface_ListW(ref hid, null, interfaces, size, 0) != 0)
            throw new IOException("The HID interface list changed. Try again.");
        string[] paths = new string(interfaces).Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => path.StartsWith(@"\\?\hid#vid_1a86&pid_ad11&", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (paths.Length != 1)
            throw new IOException("Switching to Sensors requires exactly one TURZX desktop HID interface.");

        using (var handle = CreateFileW(paths[0], 0xc0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
        {
            if (handle.IsInvalid) throw HidError("Could not open the TURZX HID interface", Marshal.GetLastWin32Error());
            IntPtr data;
            if (!HidD_GetPreparsedData(handle, out data))
                throw HidError("Could not read the TURZX HID descriptor", Marshal.GetLastWin32Error());

            HidCaps caps;
            try
            {
                int status = HidP_GetCaps(data, out caps);
                if (status != 0x00110000)
                    throw new IOException("Could not read TURZX HID report sizes (status 0x" + status.ToString("X8") + ").");
            }
            finally { HidD_FreePreparsedData(data); }

            if (caps.InputLength < 4 || caps.OutputLength < 10)
                throw new IOException("Unexpected TURZX HID report sizes (input " + caps.InputLength + ", output " + caps.OutputLength + ").");

            SendReport(handle, caps.OutputLength, 1, new byte[] { 0xaa, 0x55, 0x33 }, "send the model query");
            var response = new byte[caps.InputLength];
            uint bytesRead = 0;
            bool receivedModelReply = true;
            try { bytesRead = TransferReport(handle, response, true, "read the model reply"); }
            catch (TimeoutException) { receivedModelReply = false; }

            if (receivedModelReply && (bytesRead < 4 || response[0] != 0 || response[3] != 0x88))
                throw new IOException("The HID device did not confirm it is an 8.8-inch panel.");
            if (!receivedModelReply && !confirmUnidentifiedPanel()) return false;

            byte[] command = Encoding.ASCII.GetBytes("5f3759df");
            SendReport(handle, caps.OutputLength, 1, command, "send the first sensor-mode command");
            SendReport(handle, caps.OutputLength, 2, command, "send the final sensor-mode command");
            return true;
        }
    }

    private static void SendReport(SafeFileHandle handle, int length, int offset, byte[] payload, string operation)
    {
        var report = new byte[length];
        Buffer.BlockCopy(payload, 0, report, offset, payload.Length);
        if (TransferReport(handle, report, false, operation) != report.Length)
            throw new IOException("Incomplete HID transfer while trying to " + operation + ".");
    }

    private static uint TransferReport(SafeFileHandle handle, byte[] report, bool read, string operation)
    {
        using (var completed = new EventWaitHandle(false, EventResetMode.ManualReset))
        {
            var pinned = GCHandle.Alloc(report, GCHandleType.Pinned);
            IntPtr overlapped = IntPtr.Zero;
            try
            {
                overlapped = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeOverlapped)));
                Marshal.StructureToPtr(new NativeOverlapped { EventHandle = completed.SafeWaitHandle.DangerousGetHandle() }, overlapped, false);

                bool ready = read
                    ? ReadFile(handle, pinned.AddrOfPinnedObject(), (uint)report.Length, IntPtr.Zero, overlapped)
                    : WriteFile(handle, pinned.AddrOfPinnedObject(), (uint)report.Length, IntPtr.Zero, overlapped);
                if (!ready)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 997) throw HidError("Could not " + operation, error);
                    if (!completed.WaitOne(1250))
                    {
                        // Drain the cancelled I/O before freeing the native buffers.
                        CancelIoEx(handle, overlapped);
                        uint ignored;
                        GetOverlappedResult(handle, overlapped, out ignored, true);
                        throw new TimeoutException("Timed out while trying to " + operation + ".");
                    }
                }

                uint bytes;
                if (!GetOverlappedResult(handle, overlapped, out bytes, false))
                    throw HidError("Could not " + operation, Marshal.GetLastWin32Error());
                return bytes;
            }
            finally
            {
                if (overlapped != IntPtr.Zero) Marshal.FreeHGlobal(overlapped);
                pinned.Free();
            }
        }
    }

    private static IOException HidError(string operation, int error)
    {
        return new IOException(operation + " (Windows error " + error + ").", new Win32Exception(error));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidCaps
    {
        public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public ushort[] RemainingFields;
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, out HidCaps caps);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, uint length, IntPtr bytesRead, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle handle, IntPtr buffer, uint length, IntPtr bytesWritten, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle handle, IntPtr overlapped, out uint bytes, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    private byte[] Exchange(byte command, byte[] payload)
    {
        Write(Packet(command, payload, (int)DateTime.Now.TimeOfDay.TotalMilliseconds));
        var response = new byte[512];
        uint received;
        if (!WinUsb_ReadPipe(usb, 0x81, response, (uint)response.Length, out received, IntPtr.Zero))
            throw new Win32Exception();
        if (received == 0) throw new IOException("Empty USB response.");
        return response;
    }

    internal static byte[] Packet(byte command, byte[] payload, int milliseconds)
    {
        int length = payload == null ? 0 : payload.Length;
        var buffer = new byte[512 + length];
        var header = new byte[504];
        header[0] = command;
        header[2] = 0x1a;
        header[3] = 0x6d;
        Buffer.BlockCopy(BitConverter.GetBytes(milliseconds), 0, header, 4, 4);
        for (int offset = 0; offset < 4; offset++)
            header[8 + offset] = (byte)(length >> (24 - 8 * offset));
        using (var des = DES.Create())
        {
            des.Padding = PaddingMode.None;
            byte[] key = Encoding.ASCII.GetBytes("slv3tuzx");
            using (var encryptor = des.CreateEncryptor(key, key))
                Buffer.BlockCopy(encryptor.TransformFinalBlock(header, 0, header.Length), 0, buffer, 0, header.Length);
        }
        buffer[510] = 0xa1;
        buffer[511] = 0x1a;
        if (payload != null) Buffer.BlockCopy(payload, 0, buffer, 512, length);
        return buffer;
    }

    private void Write(byte[] packet)
    {
        uint written;
        if (!WinUsb_WritePipe(usb, 0x01, packet, (uint)packet.Length, out written, IntPtr.Zero))
            throw new Win32Exception();
        if (written != packet.Length) throw new IOException("Incomplete USB transfer.");
    }

    public void Dispose()
    {
        if (usb != IntPtr.Zero)
        {
            WinUsb_Free(usb);
            usb = IntPtr.Zero;
        }
        if (file != null) file.Dispose();
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_List_SizeW(out uint size, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_ID_ListW(string filter, [Out] char[] buffer, uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint size, ref Guid guid, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint CM_Get_Device_Interface_ListW(ref Guid guid, string id, [Out] char[] buffer, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr handle);
    [DllImport("winusb.dll")]
    private static extern bool WinUsb_Free(IntPtr handle);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPipePolicy(IntPtr handle, byte pipe, uint policy, uint length, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_WritePipe(IntPtr handle, byte pipe, byte[] data, uint length, out uint written, IntPtr overlapped);
    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_ReadPipe(IntPtr handle, byte pipe, [Out] byte[] data, uint length, out uint read, IntPtr overlapped);
}
