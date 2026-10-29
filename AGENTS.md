# Derbell - Agent Intelligence & Developer Reference (AGENTS.md)

This document provides deep technical context, architectural rules, domain quirks, and operational guidelines for AI coding agents (such as Antigravity) working on the **Derbell** codebase.

---

## 1. Project Mission & Core Constraints

Derbell is a gaming-safe Windows system tray application (.NET 8 WPF + Edge WebView2) that connects to Home Assistant to display low-latency live WebRTC video feeds of Google Nest cameras whenever a person is detected (`camera_person`).

### Non-Negotiable Constraints:
1. **Never Steal Focus**: The overlay window must **never** steal focus or alter the active window state when appearing, updating, or receiving mouse clicks. Gamers playing exclusive fullscreen or borderless-windowed games must never be tabbed out or lose input clipping.
2. **Zero External Media Dependencies**: Doorbell audio alerts are synthesized purely in-memory as PCM WAV streams; do not depend on external `.wav` files that can be missing or locked by other processes.
3. **Graceful Build Workflow**: The project includes automated build hooks to shut down running tray instances before MSBuild copies binaries, preventing file-locking compile errors (`MSB3027`).

---

## 2. Codebase Map & Responsibilities

```
D:\code\derbell\
├── Derbell.sln
├── nuget.config                            # Direct nuget.org package source configuration
├── README.md                               # Human-facing documentation
├── AGENTS.md                               # AI agent intelligence reference (this file)
├── src/
│   └── Derbell/
│       ├── Derbell.csproj                  # .NET 8 WPF + WinForms; WebView2; StopRunningInstanceBeforeCopy hook
│       ├── app.manifest                    # PerMonitorV2 DPI awareness declaration
│       ├── App.xaml / App.xaml.cs          # Single-instance mutex; CLI flags (--shutdown, --self-test)
│       ├── Interop/
│       │   ├── Win32WindowHelper.cs        # P/Invoke for WS_EX_NOACTIVATE, HWND_TOPMOST, WM_MOUSEACTIVATE
│       │   └── MonitorHelper.cs            # Multi-monitor enumeration and coordinate calculation
│       ├── Models/
│       │   └── AppConfig.cs                # Configuration persistence (%APPDATA%\Derbell\config.json)
│       ├── Services/
│       │   ├── HomeAssistantClient.cs      # HA WebSocket & REST client; WebRTC signaling; camera discovery
│       │   ├── PersonDetectionController.cs# Multi-camera state machine, presence watchdog, baseline token seeding
│       │   ├── ChimeSoundService.cs        # In-memory dual-tone PCM doorbell synthesis & playback
│       │   └── TrayIconService.cs          # Windows Forms tray icon, dynamic status dots, context menus
│       └── Views/
│           ├── PopupWindow.xaml / .cs      # No-activate overlay; WebView2 WebRTC player; HUD controls
│           └── SettingsWindow.xaml / .cs   # Setup UI; multi-camera checklist; placement drag preview
└── tests/
    └── Derbell.Tests/
        ├── Derbell.Tests.csproj
        └── Program.cs                      # 10 automated headless verification tests
```

---

## 3. Home Assistant & Google Nest Domain Quirks

### 3.1 Entity Naming for Nest Cameras
- Home Assistant's Google Nest integration (using the Google Smart Device Management / SDM API) **does not create `event.<camera>_person` entities**.
- Instead, it creates a unified motion event entity:
  ```
  event.<camera_name>_motion
  ```
  *(Example: `event.front_porch_camera_motion`)*.
- The entity has an attribute `event_types` containing `["camera_motion", "camera_person", "camera_sound"]`.

### 3.2 Two-Stage Event Evolution (Critical Bug Prevention)
- When a person walks into view of a Nest camera, the Google SDM cloud pipeline almost always fires a general motion event first:
  1. Entity state changes to a timestamp with `attributes.event_type = "camera_motion"`.
  2. A fraction of a second later (once cloud object recognition classifies the bounding box), the entity updates again with `attributes.event_type = "camera_person"`.
- **Trap to Avoid**: In many cases, the entity's main `state` string (the event timestamp) or sequence ID does not drastically change between these two events.
- **Rule**: Never ignore events if `old_state.state == new_state.state`. You must check whether `attributes.event_type == "camera_person"`.

### 3.3 Event Subscriptions: WebSocket vs REST Polling
- In `HomeAssistantClient.cs`, two WebSocket event streams are subscribed upon connection:
  1. `state_changed`: Catches updates to `event.<camera>_motion` entities.
  2. `nest_event`: Catches raw Nest SDM Pub/Sub events delivered directly to Home Assistant.
- **Fail-Safe Polling**: A background timer polls `/api/states/event.<camera>_motion` every 1.5 seconds. If a WebSocket packet drops or reconnects during a person trigger, the poller compares the entity's `last_updated` / `last_changed` / `event_type` tokens against the baseline and triggers detection.
- **Baseline Seeding**: On connection startup, `PersonDetectionController` seeds the current token of all configured cameras so that historical state already present on Home Assistant boot does not cause an immediate false-positive popup.

### 3.4 Camera Proxy Black Frame Pitfall
- In Home Assistant, calling `/api/camera_proxy/camera.<camera_name>` for Nest cameras streaming via WebRTC returns a **solid black placeholder JPEG** (1x1 or static black frame), NOT a real video frame snapshot.
- Attempting to display camera thumbnails via `/api/camera_proxy` will show an empty black box.
- All visual feeds must be established through the native Home Assistant WebRTC pipeline.

---

## 4. WebRTC Video Pipeline & SDM Constraints

### 4.1 Native Home Assistant WebRTC Signaling
Home Assistant 2024+ provides native WebSocket-based WebRTC signaling:
1. **Get Client Config**: Send WebSocket command `camera/webrtc/get_client_config` with `entity_id: "camera.<name>"`. Home Assistant returns STUN and TURN server credentials. (Crucial for remote setups, Nabu Casa URLs, and symmetric NAT traversal).
2. **SDP Offer**: Create the local SDP offer in WebView2 and send `camera/webrtc/offer` with the offer SDP.
3. **SDP Answer**: Home Assistant negotiates with Google Nest SDM and returns the answer SDP.
4. **Trickle ICE**: Exchange candidates via `camera/webrtc/candidate` as they are discovered.

### 4.2 Nest SDM Transceiver Ordering (Strict SDP Syntax)
Google Nest SDM endpoints enforce strict SDP m-line ordering:
1. Audio transceiver **must** be added first:
   ```javascript
   pc.addTransceiver('audio', { direction: 'recvonly' });
   ```
2. Video transceiver **must** be added second:
   ```javascript
   pc.addTransceiver('video', { direction: 'recvonly' });
   ```
3. A data channel named `dataSendChannel` must be opened:
   ```javascript
   pc.createDataChannel('dataSendChannel');
   ```
*If audio and video transceivers are inverted or if the data channel is omitted, Google SDM rejects the SDP offer with a 400 Bad Request or hangs indefinitely.*

### 4.3 Nest Stream Lifetime & Recycling
- Google Nest SDM WebRTC live streams are hard-terminated on the cloud side after approximately **5 minutes**.
- Derbell automatically tears down and closes `RTCPeerConnection` instances when the popup hides.
- Never attempt to reuse a WebRTC connection older than 60 seconds without renegotiation.

---

## 5. Win32 Overlay & Gaming-Safe Focus Rules

### 5.1 Extended Window Styles
WPF's built-in `Topmost="True"` and `ShowActivated="False"` are insufficient for gaming overlays because Windows will still transfer focus on show or click under various DWM compositing circumstances.
`Win32WindowHelper.MakeNoActivateTopmost(IntPtr hwnd)` applies:
- `WS_EX_NOACTIVATE (0x08000000)`: Prevents the window from becoming active when displayed or clicked.
- `WS_EX_TOOLWINDOW (0x00000080)`: Hides the window from the Windows taskbar and Alt+Tab switcher.
- `WS_EX_TOPMOST (0x00000008)`: Forces Z-order above standard application windows.

### 5.2 Critical Win32 API Quirk: SetWindowLong vs SetWindowPos
- **Quirk**: Calling `SetWindowLongPtr(hwnd, GWL_EXSTYLE, currentStyle | WS_EX_TOPMOST)` is **ignored by the Windows Window Manager for Z-ordering**.
- **Fix**: To actually make a window topmost without activating it, you **must** call `SetWindowPos`:
  ```csharp
  SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
  ```

### 5.3 Mouse Activation Interception
In `PopupWindow.xaml.cs`, `HwndSource.AddHook` intercepts `WM_MOUSEACTIVATE (0x0021)`:
```csharp
if (msg == WM_MOUSEACTIVATE)
{
    handled = true;
    return new IntPtr(MA_NOACTIVATE); // 3
}
```
This guarantees that clicking UI buttons (such as **✕ Hide** or **Mute**) on the popup does not steal foreground focus from an active game.

### 5.4 WebView2 Rendering & Visibility Rules
- WebView2 is an `HwndHost` Win32 child window inside the WPF visual tree.
- **Crucial Quirk**: If `WebView2.Visibility` is set to `Hidden` or `Collapsed`, Chromium completely suspends GPU rasterization, video decoding, and WebRTC media pipelines. Upon setting it back to `Visible`, the player may take 1-2 seconds to re-render or may display a blank white/black canvas.
- **Rule**: Keep `WebView2.Visibility = Visibility.Visible` continuously. Manage loading, connecting, and error states using in-DOM HTML/CSS overlays (`#status-card`, `#spinner`) rather than toggling WPF element visibility.
- For transparent WPF windows, configure `WebView2.DefaultBackgroundColor = System.Drawing.Color.Transparent`.

---

## 6. Presence Watchdog & Human Transit Timing

### 6.1 The "Walking Outside" Testing Paradox
- When a user tests person detection by walking out their front door, they are typically in front of the camera for 5-10 seconds.
- However, walking back inside and sitting down at their desk takes 15-25 seconds.
- If the popup closes immediately after person absence timeout (e.g. 5 seconds of no motion), the window will have already closed before the user returns to their desk, leading them to believe the detection failed!

### 6.2 Minimum Display Window Logic
`PersonDetectionController.cs` enforces:
1. `MinimumDisplayWindowSeconds` (default: 20 seconds, configurable up to 60s).
2. The auto-dismiss countdown timer **never fires** until at least `MinimumDisplayWindowSeconds` have elapsed since the trigger.
3. When the live WebRTC stream successfully renders its first video frame, the countdown timer resets, ensuring the user gets a full view of who was at the door.

---

## 7. In-Memory Doorbell Sound Synthesis

To maintain a zero-dependency portable binary, `ChimeSoundService.cs` synthesizes an authentic acoustic two-tone "Ding-Dong" bell entirely in memory using standard 44.1 kHz, 16-bit stereo PCM (~2.85 seconds total):
- **Tone 1 ("Ding")**: Primary note E5 (659.25 Hz) at 0.00s.
- **Tone 2 ("Dong")**: Primary note C5 (523.25 Hz) at 0.68s, sustaining for ~2.1 seconds.
- **Euler-Bernoulli Beam Modal Physics**: Struck metal chime bars vibrate with inharmonic modes rather than integer harmonics:
  - Fundamental ($f$) with ~1.6 Hz bilateral acoustic beating for natural shimmer and warmth.
  - Sub-octave hum tone ($0.5 \times f$) modeling resonator tube enclosure depth.
  - Inharmonic mode 2 ($2.756 \times f$), mode 3 ($5.404 \times f$), and mode 4 ($8.93 \times f$).
  - Solenoid plunger strike impact transient ($11.2 \times f$ and $16.4 \times f$) decaying within 15ms.
- **Entryway Foyer Acoustic Reverb**: Multi-tap early reflections (14ms, 23ms) and comb diffusion (35ms, 43ms) simulate the spacious acoustic environment of a residential entryway.
- **Volume & Normalization**: The synthesizer scans all generated samples for peak amplitude across both channels and normalizes the waveform to **95% full-scale 16-bit PCM** (`32767 * 0.95`).
- The generated bytes are wrapped in a standard 44-byte RIFF/WAV header and played through `System.Media.SoundPlayer(MemoryStream)`.

---

## 8. Build & Process Management (Preventing File Locks)

### 8.1 MSBuild Error MSB3027 Resolution
When running `dotnet build`, if an instance of `Derbell.exe` is already running in the system tray, MSBuild fails because `Derbell.exe` is locked by Windows.

In `src/Derbell/Derbell.csproj`, a custom target automatically terminates any running instance before file copying:
```xml
<Target Name="StopRunningInstanceBeforeCopy" BeforeTargets="_CopyOutOfDateSourceItemsToOutputDirectory;CopyFilesToOutputDirectory">
  <Exec Command="&quot;$(TargetPath)&quot; --shutdown"
        ContinueOnError="true"
        IgnoreExitCode="true"
        Condition="Exists('$(TargetPath)')" />
</Target>
```

### 8.2 Command-Line Flags
`Derbell.exe` supports the following execution flags:
- `--shutdown`: Signals the running instance via named `EventWaitHandle ("Derbell_Shutdown_Event")` to cleanly exit immediately.
- `--self-test`: Initializes all subsystems, verifies configuration loading, tests tray icon initialization, and exits with code 0 without showing UI.

### 8.3 Single-Instance Protection
`App.xaml.cs` acquires a named system mutex `Global\Derbell_SingleInstance_Mutex`. If another instance is running and `--shutdown` was not passed, the new process exits immediately.

---

## 9. Antigravity Sandbox & Tooling Rules

1. **PowerShell First**: Always use native PowerShell cmdlets (`Get-Process`, `Get-ChildItem`, `Test-Path`, `Invoke-RestMethod`) on Windows. Avoid Bash/Unix utilities.
2. **PowerShell `Invoke-WebRequest` Warning**: In Windows PowerShell 5.1, `Invoke-WebRequest` without `-UseBasicParsing` will fail or hang attempting to initialize Internet Explorer DOM engines. Always use `Invoke-RestMethod` or pass `-UseBasicParsing`.
3. **NuGet Restore in Sandbox**: Sandboxed environments block network access. A local `nuget.config` exists in the repository root pointing to `https://api.nuget.org/v3/index.json`. When restoring packages (`dotnet restore`), run with `BypassSandbox: true`. Subsequent builds (`dotnet build`) can execute with normal permissions.
4. **Verifying Code Changes**:
   - Run unit/integration tests:
     ```powershell
     dotnet run --project tests/Derbell.Tests
     ```
   - Self-test binary execution:
     ```powershell
     .\src\Derbell\bin\Debug\net8.0-windows\Derbell.exe --self-test
     ```
