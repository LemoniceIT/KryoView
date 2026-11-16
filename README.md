# KryoView

KryoView is an independent Windows 11 x64 project for TURZX/Turing 8.8-inch panels, with experimental support for the USB V1.x revision. Closing the window keeps KryoView in the notification area. Select **Exit** from that menu to stop it.

## Build

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` from the project root. The build uses the .NET Framework compiler included with Windows and creates `bin\KryoView.exe`. Run `KryoView.exe --self-test` to check the USB packet, device enumeration, and frame dimensions.

## Test with a panel

1. Connect a TURZX 8.8-inch USB V1.x panel and select **Sensors**. Choose a landscape or portrait skin from the orientation menu. Both layouts offer flipped variants. The dashboard shows CPU, GPU, RAM, CPU threads, used and available RAM, free space on the system drive, inbound and outbound network traffic, uptime, and power status. Readings appear on the panel. GPU usage reflects the busiest GPU engine exposed by Windows. A dash indicates an unavailable reading.
2. For monitor mode, select **Install TURZX display driver** from the notification area menu and choose the INF from the official desktop driver package. Extract all files together, including the signed catalog. Windows requests administrator privileges to install the package with PnPUtil. Obtain the driver package from the manufacturer.
3. Select **USB monitor**, then open **Display settings** to choose **Extend** and set the monitor orientation.
4. Select **Sensors** to return to the dashboard. KryoView sends the HID command after the device confirms that it is an 8.8-inch panel.

Switching modes and installing the official package still require hardware testing. KryoView detects Rev C panels. Dashboard and monitor mode are implemented for the USB V1.x revision. Monitor mode requires the manufacturer's display driver and can run with the TURZX app closed.

[Panel protocol](https://github.com/jripper77/bezel-evo/blob/windows-improvements/docs/reverse-engineering/protocol-turing-usb.md#10-desktop-mode-and-the-hid-companion-1a86ad11) · [Managing driver packages with PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)
