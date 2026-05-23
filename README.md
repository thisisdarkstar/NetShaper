# ⚡ NETSHAPER

An elegant, hardware-accelerated **PySide6** desktop bandwidth management suite for Windows. **NetShaper** acts as a system-wide traffic interception and bandwidth throttling controller using a built-in high-performance SOCKS5 proxy engine and dynamic Token Bucket rate limiters.

![NetShaper Banner](netshaper_icon.png)

## ✨ Features

- **🚀 System-Wide Interception**: Dynamically reroutes and intercepts network traffic via a locally-bound SOCKS5 engine with registry integration.
- **🔍 Active Socket Inspector**: Double-click any running process in the real-time activity grid to spawn an inspector window detailing all active TCP/IP sockets (`Direct` vs. `Shaped`).
- **⏳ Token Bucket Shaping**: Smooth, modern multi-threaded token bucket bandwidth regulation with zero rate stuttering.
- **⚡ Premium Hover UX**: High-impact UI featuring curated dark HSL palettes, clean layouts, and micro-animated slider toggles with pointer hand cursors on all interactive elements.
- **🔒 Persistent Policies**: Save, load, and edit bandwidth cap rules (Download / Upload limits in KB/s, MB/s, Mbps, or Kbps) stored securely inside your global user AppData folder (`%APPDATA%\NetShaper\shaper_rules.json`) so your rules persist regardless of execution path.

---

## 🛠️ Technology Stack

- **Core Framework**: PySide6 (Qt 6 for Python)
- **Calculations & Process Hooks**: `psutil` & elevated `ctypes` Windows Registry hooks
- **Interception Server**: Multi-threaded socket-level SOCKS5 proxy implementation
- **Packaging**: Single-file PyInstaller elevated compilation (`--uac-admin`)

---

## 💻 Running the Application

### Option A: Standalone Executable (Recommended)
You can directly run the pre-built, production-ready standalone executable with the custom embedded icon.
1. Navigate to the **GitHub Releases** tab of this repository and download **`net_shaper.exe`**.
2. Right-click **`net_shaper.exe`** and select **Run as Administrator** (required for system-wide registry interception hooks).

### Option B: Build from Source
To execute the source script or build it inside a local Python environment:

1. Clone this repository:
   ```bash
   git clone https://github.com/thisisdarkstar/NetShaper.git
   cd NetShaper
   ```

2. Initialize and activate virtual environment:
   ```bash
   python -m venv .venv
   .venv\Scripts\activate
   ```

3. Install required dependencies:
   ```bash
   pip install -r requirements.txt
   ```

4. Launch the application:
   ```bash
   python net_shaper.py
   ```

---

## 🔧 Building Standalone Binary
To compile a brand new elevated single-file executable with custom icon bundles:
```bash
pip install pyinstaller
pyinstaller --clean --onefile --noconsole --uac-admin --icon netshaper_icon.ico --add-data "netshaper_icon.png;." -n net_shaper net_shaper.py
```

---

## 📄 License & Terms
*Created for secure local bandwidth regulating, testing, and hardware network diagnostic monitoring.*
