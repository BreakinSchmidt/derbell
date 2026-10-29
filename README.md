# Derbell 🔔

**Derbell** is a lightweight, gaming-safe Windows system tray application built in **.NET 8 WPF + Edge WebView2**. It connects to **Home Assistant** and automatically pops up live **Google Nest** camera feeds when a person is detected (`camera_person`), without stealing focus, altering active window state, or interrupting your mouse/keyboard/controller in fullscreen or borderless-windowed games.

---

## Key Features

- 🎮 **Gaming-Safe Overlay**:
  - Built with Win32 extended styles `WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`.
  - Displayed via `ShowWindow(SW_SHOWNOACTIVATE)` and `SetWindowPos(HWND_TOPMOST, ..., SWP_NOACTIVATE)`.
  - Intercepts `WM_MOUSEACTIVATE` to return `MA_NOACTIVATE`—clicking the popup's **✕ Hide** or **Mute** buttons will **never** tab you out of your game or unclip your mouse.
  - Optional **Click-Through Mode** (`WS_EX_TRANSPARENT`) where all mouse clicks pass straight through the video to whatever game or app is behind it.
  - Global Dismiss Hotkey (default `Ctrl+Shift+X`, configurable) to dismiss mid-game without touching the mouse.

- 📹 **Multi-Camera Nest Support**:
  - Connects to Home Assistant via WebSocket and automatically discovers all your Nest cameras.
  - Automatically correlates cameras with their Nest `device_id` and built-in motion/person event entities (`event.<camera>_motion`).
  - Checklist UI lets you monitor multiple cameras simultaneously.
  - Dynamically routes alerts to the specific camera where a person was detected and switches feeds seamlessly if a second camera triggers.

- 🔔 **Classic "Ding-Dong" Doorbell Sound**:
  - In-memory synthesized two-tone electro-mechanical chime (`E5 -> C5`, ~2.85s) with authentic Euler-Bernoulli inharmonic bar modes, plunger strike transient, natural ~1.6Hz acoustic beating, and residential foyer reverberation.
  - Configurable toggle, volume slider, and live preview in Settings. Zero external `.wav` files required.

- ⚡ **Low-Latency WebRTC Video Streaming**:
  - Pre-warmed Edge `WebView2` hardware-accelerated video pipeline.
  - Queries Home Assistant's `camera/webrtc/get_client_config` for STUN/TURN servers (works seamlessly over local connections and Nabu Casa remote URLs).
  - Handles two-way trickle ICE candidate exchange (`camera/webrtc/candidate`).

- 🛡️ **Dual-Engine Event Detection**:
  - **Real-time WebSocket**: Subscribes directly to `nest_event` and `state_changed` on the HA event bus (<50ms response).
  - **Background State Poller**: Periodically checks `event.<camera>_motion` state timestamps every 1.5s as a fail-safe against network hiccups or non-admin token restrictions.
  - Handles Nest's multi-stage event progression (e.g., initial `camera_motion` upgrading to `camera_person` within the same event session).

- 🖥️ **Multi-Monitor & DPI Aware**:
  - Fully `PerMonitorV2` DPI-aware for mixed scaling environments (1080p, 1440p, 4K).
  - Corner presets (`TopRight`, `BottomRight`, `TopLeft`, `BottomLeft`) or pixel-accurate custom coordinates.
  - Interactive **Placement Preview Mode**: drag and drop the popup anywhere on any monitor and save the position.

---

## Requirements

- **Operating System**: Windows 10 (1809+) or Windows 11
- **Runtime**: [.NET 8.0 Windows Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- **Home Assistant**: Home Assistant with the [Google Nest integration](https://www.home-assistant.io/integrations/nest/) configured and active Google Home Premium / Nest Aware subscription for person detection.
- **Home Assistant Token**: A Long-Lived Access Token (generated in Home Assistant: Profile → Security → Long-Lived Access Tokens).

---

## Quick Start

### Running from Pre-built Binary
Run:
```powershell
.\src\Derbell\bin\Debug\net8.0-windows\Derbell.exe
```

### Building from Source
```powershell
git clone https://github.com/breakinschmidt/derbell.git
cd derbell
dotnet build Derbell.sln
dotnet run --project src\Derbell
```

---

## Configuration

On first launch, the **Settings** window will open automatically. You can also double-click the camera icon in the Windows System Tray or right-click → **Settings...** anytime.

1. **Home Assistant Connection**:
   - Enter your Home Assistant URL (e.g. `http://homeassistant.local:8123` or your `https://xxxx.ui.nabu.casa` remote URL).
   - Paste your Long-Lived Access Token.
   - Click **Connect & Discover Cameras**.
2. **Camera Selection**:
   - Check the boxes for the Nest cameras you want to monitor.
3. **Monitor & Placement**:
   - Select your target monitor from the dropdown.
   - Choose a corner preset or click **Preview & Drag Position on Screen** to drag the window to your exact preferred spot.
4. **Behavior**:
   - **Doorbell Sound**: Toggle on/off and set volume (click **🔊 Preview Doorbell** to test).
   - **Hide After No Person Activity**: Minimum time to keep feed visible (default 20 seconds; resets when video starts playing).
   - **Click-Through Mode**: Pass all mouse clicks through the window directly to the underlying application.
   - **Global Dismiss Hotkey**: Default `Ctrl+Shift+X`.
   - **Start with Windows**: Automatically start Derbell minimized to tray on Windows boot.

---

## Architecture Overview

```
                        ┌──────────────────────────────────────────────┐
                        │               Google Nest Cloud              │
                        │         (Smart Device Management API)        │
                        └──────────────────────┬───────────────────────┘
                                               │ Pub/Sub & WebRTC SDP
                                               ▼
                        ┌──────────────────────────────────────────────┐
                        │                Home Assistant                │
                        │       (WebSocket API + WebRTC Gateway)       │
                        └──────────────────────┬───────────────────────┘
                                               │
                       WebSocket /api/websocket │ REST /api/states (1.5s fallback)
                                               ▼
┌────────────────────────────────────────────────────────────────────────────────────────┐
│                                     Derbell                                            │
│                                                                                        │
│   ┌──────────────────────────┐                  ┌──────────────────────────────────┐   │
│   │   HomeAssistantClient    │                  │    PersonDetectionController     │   │
│   │  - Auth & Keep-Alive     ├─────────────────►│  - Strict camera_person filter   │   │
│   │  - Event subscriptions   │  State / Events  │  - Multi-camera event router     │   │
│   │  - WebRTC signaling      │                  │  - Presence watchdog timer       │   │
│   └─────────────┬────────────┘                  └────────────────┬─────────────────┘   │
│                 │                                                │                     │
│                 │ SDP Offer / Answer                             │ Show / Hide         │
│                 ▼                                                ▼                     │
│   ┌────────────────────────────────────────────────────────────────────────────────┐   │
│   │                       PopupWindow (WPF + Win32 Interop)                        │   │
│   │  - WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW                        │   │
│   │  - WM_MOUSEACTIVATE -> MA_NOACTIVATE                                           │   │
│   │  - WebView2 WebRTC Player with hardware video decode                           │   │
│   │  - Synthesized "Ding-Dong" Doorbell Chime (ChimeSoundService)                  │   │
│   └────────────────────────────────────────────────────────────────────────────────┘   │
└────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Testing

Run the included automated test suite:
```powershell
dotnet test
# or run the standalone test runner:
.\tests\Derbell.Tests\bin\Debug\net8.0-windows\Derbell.Tests.exe
```

Test suite coverage:
- Strict `camera_person` event filtering (ignoring general motion/sound/chime).
- Active-session presence watchdog timer extension and auto-hide.
- Multi-camera routing and active camera switching.
- Per-camera manual dismiss cooldown enforcement.
- Snooze / pause behavior.
- Home Assistant `nest_event` and `state_changed` JSON payload parsing.
- URL normalization (HTTP, HTTPS, WS, WSS).
- Multi-monitor coordinate math and boundary clamping.
- Win32 extended style application and hotkey parser.
- In-memory doorbell chime WAV synthesis and sample verification.

---

## License

MIT License. See [LICENSE](LICENSE) for details.
