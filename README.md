# AI Core Monitor

AI Core Monitor is a glassy, scalable Windows 11 desktop widget for local AI workstation telemetry. It uses .NET 10, WinUI 3, Windows App SDK, and Windows Composition.

## Current telemetry

- **Codex:** rolling-window allowance, reset time, plan, and current context tokens from `%USERPROFILE%\.codex\sessions`.
- **NVIDIA GPU:** utilization, VRAM, temperature, power, and a live utilization sparkline through local `nvidia-smi`.
- **Ollama:** service state, installed and loaded model counts, model storage, and the active model through `http://127.0.0.1:11434`.

Providers are isolated and independently timed out. An unavailable GPU or Ollama service does not prevent Codex telemetry from updating.

## Run

Double-click `RunWidget.cmd`. It selects the published native executable first and falls back to running the WinUI project from source when necessary.

Current published executable:

```text
artifacts\publish\winui-fx-menu\AiCoreMonitor.exe
```

The publish is a self-contained Windows x64 folder and does not require a separately installed .NET or Windows App SDK runtime on the target machine. Folder deployment is intentional: WinUI single-file extraction materially delays widget startup.

## Controls

- Drag the header to reposition the widget.
- Resize continuously from 340 x 440 through 920 x 1120 logical pixels.
- Typography and card density adapt at compact widths using real font sizes; the complete window is never bitmap-scaled.
- Click `FX` for compact Lava and Cracks rows with an independent amount slider for each effect. Moving the widget automatically re-enables both effects.
- Click `-` to minimize and `x` to exit.

Window position, dimensions, per-effect state, intensity, and topmost state persist in `%LOCALAPPDATA%\AiCoreMonitor\settings.json`.

## Visual and window architecture

- WinUI 3 adaptive XAML on Windows App SDK 1.8.
- Windows 11 Desktop Acrylic backdrop.
- DWM-owned top-level rounded corners and native shadow; the main window is not a layered transparency window.
- Per-monitor-v2 DPI awareness.
- DirectWrite text at every size, continuous responsive layout, and no whole-window scale transform.
- No scroll viewers or scrollbars at any size.
- GPU-driven Windows Composition glow fields, sparkline, branching top-origin lava fractures, and molten edge flows.
- A separate no-activate, click-through Win32 layered HWND renders continuous path-based lava across the panel and beyond its lower boundary.
- The external renderer uses animated viscous Bézier bodies, variable necks, molten bulbs, internal highlights, surface bubbles, and released droplets; it pauses automatically when disabled or minimized.

## Build and test

The solution targets .NET 10:

```powershell
$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"
dotnet build .\AiCoreMonitor.sln -c Release
dotnet test .\AiCoreMonitor.sln -c Release
```

Create the self-contained deployment:

```powershell
dotnet publish .\src\AiCoreMonitor.WinUI\AiCoreMonitor.WinUI.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:Platform=x64 -p:PublishSingleFile=false -p:EnableMsixTooling=true `
    -o .\artifacts\publish\winui-release
```

## Source layout

```text
src
|-- AiCoreMonitor.Core
|   |-- Core             normalized contracts and snapshots
|   |-- Providers        Codex, NVIDIA, and Ollama collectors
|   |-- Services         isolation, cancellation, and timeouts
|   |-- ViewModels       presentation-neutral observable state
|   `-- Infrastructure   persisted settings
|-- AiCoreMonitor.WinUI
|   |-- Presentation     Windows Composition renderers
|   `-- Interop          DWM and external overlay HWND
`-- AiCoreMonitor.Tests
```

## Privacy

The application does not read `auth.json`, browser cookies, or account credentials. Ollama access is restricted to localhost. It sends no telemetry externally. OpenAI API organization usage and costs will be added as an explicit opt-in provider using Windows Credential Manager; ChatGPT consumer-account scraping will not be used.
