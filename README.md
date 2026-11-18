# KryoView

KryoView is an independent Windows 11 x64 utility for a TURZX 8.8-inch USB V1.x panel. The project was vibe-coded with AI assistance and iterated using hands-on feedback from the connected panel.

Hardware validation covers one TURZX 8.8-inch USB V1.x unit connected via USB (VID `1CBE`, PID `0088`). Sensors, USB monitor mode, and switching back to Sensors were confirmed on that unit. Other hardware revisions, panel models, and Windows versions remain unverified.

Closing the window keeps KryoView in the notification area. Select **Exit** there to stop it.

## Dashboard previews

KryoView rendered these previews using sample readings.

![Blueberry Arc dashboard](docs/screenshots/blueberry.png)
![Harbor Dark dashboard](docs/screenshots/harbor.png)
![Amber Terminal dashboard](docs/screenshots/amber.png)

## Build

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1` from the project root. The build uses the .NET Framework compiler included with Windows and creates `bin\KryoView.exe`. Run `KryoView.exe --self-test` to check the USB packet, device enumeration, and frame dimensions.

## Test with a panel

1. Connect a TURZX 8.8-inch USB V1.x panel and select **Sensors**. Choose a landscape or portrait layout under **Options → Orientation**. Both layouts offer flipped variants. The dashboard shows CPU, GPU, RAM, CPU threads, used and available RAM, free space on the system drive, inbound and outbound network traffic, uptime, and power status. Readings appear on the panel. GPU usage reflects the busiest GPU engine exposed by Windows. A dash indicates an unavailable reading.
2. For monitor mode, select **Install TURZX display driver** from the notification area menu and choose the INF from the official desktop driver package. Extract all files together, including the signed catalog. Windows requests administrator privileges to install the package with PnPUtil. Obtain the driver package from the manufacturer.
3. Select **USB monitor**, then open **Display settings** to choose **Extend** and set the monitor orientation.
4. Select **Sensors** to return to the dashboard. KryoView sends the HID command after the device confirms that it is an 8.8-inch panel.

KryoView detects Rev C panels. Monitor mode requires the manufacturer's display driver and can run with the TURZX app closed.

## Sensor themes and logs

Open **Sensor themes** to choose Classic, Blueberry Arc, Harbor Dark, or Amber Terminal. To add a theme, copy a `.theme` file in the `themes` folder next to the executable, then edit it. Each file has eight lines. The first is the display name, followed by seven `#RRGGBB` colors in this order: background, card surface, border, foreground, CPU, GPU, and RAM. Remove the file to remove its menu entry. If a saved theme is absent at startup, KryoView opens with Classic.

**Hide USB packet transfers** filters the terminal while keeping status messages and errors visible. Clear the checkbox to show recent packet timings again.

## License

KryoView is released under the [MIT License](LICENSE). Retain the copyright and permission notices in all copies or substantial portions of the software.

[Panel protocol](https://github.com/jripper77/bezel-evo/blob/windows-improvements/docs/reverse-engineering/protocol-turing-usb.md#10-desktop-mode-and-the-hid-companion-1a86ad11) · [Managing driver packages with PnPUtil](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-command-syntax)
