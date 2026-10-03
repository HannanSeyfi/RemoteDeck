# RemoteDeck

RemoteDeck is a local-Wi-Fi remote control surface for a Windows PC, accessed from a browser on another device. It serves its UI locally, authenticates devices with a pairing code and long-lived HTTP-only cookie, limits control to one WebSocket at a time, and injects only predefined mouse, keyboard, text, and media commands through `SendInput`. The touchpad gestures are designed for touchscreen browsers, including phones and tablets.

## Build and run

The project targets `net10.0-windows` with Windows Forms for the tray icon. A supported .NET SDK is required. From this directory:

```powershell
dotnet build -c Release
dotnet run
```

Right-click the RemoteDeck tray icon and choose **Show connection details**. Open one of the displayed `http://<private-ip>:8765` addresses in a browser on a device connected to the same trusted Wi-Fi. Pair using the displayed eight-digit code.

The process must run in the signed-in interactive Windows session. It is deliberately not a Windows Service and cannot control UAC's secure desktop or a higher-integrity administrator window.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o .\publish
```

The whole `publish` directory is required because the browser assets are deployed under `wwwroot`.

After publishing, run `.setup-firewall.ps1` from an elevated PowerShell prompt. It removes the development rule and creates a rule restricted to TCP 8765, Private networks, the LocalSubnet, and the published executable. Run `.install-startup.ps1` to add a per-user startup shortcut.

## Security boundaries

Use only on a trusted Private home network. The first version uses HTTP/WebSocket rather than encrypted HTTPS/WSS, so traffic is not suitable for shared Wi-Fi. Never expose port 8765 through the router. Revoke all devices from the tray menu if a paired device is lost; trusted-device hashes are stored in `%LOCALAPPDATA%\RemoteDeck\trusted-devices.json`.

The application does not execute shell commands, accept arbitrary file paths, load external JavaScript, synchronize clipboards, or provide screen streaming. Those features require separate security and transport design.
