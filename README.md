# ⚡ NETSHAPER

An elegant, hardware-accelerated **C# / .NET** desktop bandwidth management suite built natively for Windows. **NetShaper** provides real-time system socket inspection, process-level bandwidth throttling using an asynchronous SOCKS5 proxy engine, transparent SNI TLS interceptor, and high-precision, non-blocking Token Bucket rate limiters.

![NetShaper Banner](src/NetShaper/Resources/netshaper_icon.png)

## ✨ What's New in v1.0.0 (Official Windows Release)

- **🚀 Native Windows Performance**: Rewritten from scratch in C# / .NET 8 targeting the Windows Desktop runtime with DirectX hardware acceleration.
- **🎯 Direct Game & CDN Throttler (Vanguard Anti-Cheat Safe)**: Built-in transparent SNI TLS interceptor (`127.0.0.1:443`) and dynamic DNS redirection. Throttles game launchers (Riot Client / Valorant), download managers, and direct socket connections safely with zero DLL injection, zero kernel drivers, and 100% end-to-end TLS certificate validity.
- **📊 Real-Time ETW Kernel Socket Monitor**: Direct Windows Kernel Event Tracing captures OS-wide network throughput (TCP and UDP) per process ID with microsecond latency.
- **🛡️ Auto-Heal & Crash Recovery**: NetShaper automatically detects and fixes stale proxy configurations and hosts entries upon startup or abnormal exit, ensuring internet connectivity is never left broken.
- **⏳ True Non-Blocking Token Bucket**: Eliminates thread contention and stuttering. Sockets calculate fractional rate delays and wait asynchronously without locking out other connections.
- **🔍 Native IP Helper Socket Inspector**: Directly queries Windows `iphlpapi.dll` (`GetExtendedTcpTable`) to retrieve active TCP sockets and owning process IDs with zero disk I/O interference.
- **⚡ Silky-Smooth UI & Virtualization**: Full DataGrid row recycling virtualization and optimized controls ensure 60 FPS performance, zero lag during scrolling, and instant dropdown menus.
- **🔔 System Tray Integration**: Minimizes seamlessly to the Windows System Tray while actively shaping traffic in the background.
- **🔒 Persistent Policies**: Saves and loads policies stored in your user AppData directory (`%APPDATA%\NetShaper\shaper_rules.json`).

---

## 🛠️ Technology Stack

- **Platform**: C# 12 / .NET 8+ Windows Desktop (WPF, DirectX Hardware Accelerated)
- **Windows Subsystem APIs**: 
  - `iphlpapi.dll`: Native TCP socket table enumeration (`GetExtendedTcpTable`) and socket termination (`SetTcpEntry`)
  - `wininet.dll`: Real-time system proxy notification hooks (`InternetSetOption`)
  - `Microsoft.Diagnostics.Tracing.TraceEvent`: Kernel ETW network packet monitoring
- **Networking Engine**: Asynchronous dual-mode SOCKS5 / HTTP proxy & Transparent SNI TLS/HTTP forwarder
- **Throttling Algorithm**: Microsecond-precision fractional Token Bucket with cumulative debt scheduling

---

## 💻 Building & Running

### Requirements
- Windows 10 (1809+) or Windows 11 (x64)
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or higher

### Option A: Run from Source
```bash
# Clone the repository
git clone https://github.com/thisisdarkstar/NetShaper.git
cd NetShaper

# Build and run the project
dotnet run --project src/NetShaper/NetShaper.csproj
```

### Option B: Publish Single-File Executable
To produce an optimized single-file executable (`publish/NetShaper.exe`):
```bash
dotnet publish src/NetShaper/NetShaper.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/
```

Or for a fully self-contained standalone binary:
```bash
dotnet publish src/NetShaper/NetShaper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/
```

---

## 📂 Architecture Overview

```
NetShaper/
├── NetShaper.sln                            # Solution file
├── src/
│   └── NetShaper/
│       ├── Core/
│       │   ├── BandwidthRegulator.cs        # Non-blocking Token Bucket limiter
│       │   ├── Socks5ProxyServer.cs         # Async dual-mode SOCKS5/HTTP proxy engine
│       │   ├── TransparentSniInterceptor.cs # Raw TLS SNI transparent stream interceptor
│       │   ├── HostsRedirectManager.cs      # Dynamic DNS redirection & cleanup
│       │   ├── EtwNetworkMonitor.cs         # Windows Kernel ETW socket monitor
│       │   ├── NetworkMonitor.cs            # iphlpapi.dll P/Invoke TCP inspector
│       │   ├── WinInetProxyManager.cs       # Windows Registry & auto-heal recovery
│       │   ├── WindowsQosManager.cs         # Policy-based QoS outbound packet scheduler
│       │   ├── SystemTrayManager.cs         # Windows NotifyIcon tray integration
│       │   ├── RuleManager.cs               # JSON persistence in %APPDATA%
│       │   └── AppLauncher.cs               # Isolated process launcher with proxy routing
│       ├── Models/
│       │   ├── ShaperRule.cs                # Bandwidth rule models & unit parsing
│       │   ├── ProcessNetworkInfo.cs        # Process monitor data model (INotifyPropertyChanged)
│       │   └── ActiveSocketInfo.cs          # Active socket inspector model
│       ├── Controls/
│       │   └── ToggleSwitch.xaml            # Animated slider toggle switch
│       ├── Views/
│       │   ├── SocketsInspectorWindow.xaml  # Socket inspector popup window
│       │   └── ToastNotification.xaml       # Non-blocking floating toast cards
│       ├── MainWindow.xaml                  # Primary dashboard & process monitor
│       ├── app.manifest                     # UAC requireAdministrator & DPI awareness
│       └── Resources/                       # Embedded icons and branding
```

---

## 📄 License & Terms
*Created for secure local bandwidth regulating, testing, and hardware network diagnostic monitoring.*
