using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Derbell.Interop;
using Derbell.Models;
using Derbell.Services;

namespace Derbell.Tests;

public static class Program
{
    private static int _passed;
    private static int _failed;

    [STAThread]
    public static int Main()
    {
        Console.WriteLine("=== Derbell Automated Test Suite ===");

        RunTest("1. Strict camera_person filter ignores motion/sound/chime when closed", TestStrictPersonFilter);
        RunTest("2. Active session extends presence watchdog on continuation events", TestPresenceExtensionAndTimeout);
        RunTest("3. Multi-camera routing & switching between cameras on person detection", TestMultiCameraRoutingAndSwitching);
        RunTest("4. Per-camera manual dismiss cooldown (dismissing Cam A does not block Cam B)", TestPerCameraManualDismissCooldown);
        RunTest("5. Snooze mode hides active popup and suppresses alerts until resumed", TestSnoozeMode);
        RunTest("6. Home Assistant nest_event and state_changed JSON parsing", TestHomeAssistantJsonParsing);
        RunTest("7. Home Assistant WebSocket and HTTP URL normalization", TestUrlNormalization);
        RunTest("8. Multi-monitor placement presets and boundary clamping", TestMonitorPlacementCalculations);
        RunTest("9. Hotkey parser and Win32 WS_EX_NOACTIVATE non-activating window styles", TestWin32StylesAndHotkeys);
        RunTest("10. Relaxing acoustic chime WAV synthesis (RIFF header & zero-click envelope)", TestRelaxingChimeSynthesis);

        Console.WriteLine($"\nResults: {_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void RunTest(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Console.WriteLine($"  [PASS] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}: {ex.Message}");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} (Expected: {expected}, Actual: {actual})");
        }
    }

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TestStrictPersonFilter()
    {
        var config = new AppConfig
        {
            PersonAbsenceTimeoutSeconds = 20,
            Cameras = new List<MonitoredCameraConfig>
            {
                new() { EntityId = "camera.front_yard", FriendlyName = "Front Yard", DeviceId = "dev1", Enabled = true }
            }
        };
        var controller = new PersonDetectionController(config);
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        AssertEqual(
            DetectionDecision.Ignore,
            controller.ProcessNestEvent(new NestEventPayload("dev1", "camera_motion", "ev1", null), t0),
            "camera_motion should be ignored when closed");

        AssertEqual(
            DetectionDecision.Ignore,
            controller.ProcessNestEvent(new NestEventPayload("dev1", "camera_sound", "ev2", null), t0),
            "camera_sound should be ignored");

        AssertEqual(
            DetectionDecision.Ignore,
            controller.ProcessNestEvent(new NestEventPayload("dev1", "doorbell_chime", "ev3", null), t0),
            "doorbell_chime should be ignored");

        AssertTrue(!controller.IsPopupVisible, "Popup must still be hidden");

        AssertEqual(
            DetectionDecision.ShowPopup,
            controller.ProcessNestEvent(new NestEventPayload("dev1", "camera_person", "ev4", null), t0),
            "camera_person must trigger ShowPopup");

        AssertTrue(controller.IsPopupVisible, "Popup must be visible after camera_person");
        AssertEqual("camera.front_yard", controller.ActiveCamera?.EntityId, "ActiveCamera matches Front Yard");
    }

    private static void TestPresenceExtensionAndTimeout()
    {
        var config = new AppConfig
        {
            PersonAbsenceTimeoutSeconds = 20,
            Cameras = new List<MonitoredCameraConfig>
            {
                new() { EntityId = "camera.front_yard", FriendlyName = "Front Yard", DeviceId = "dev1", Enabled = true }
            }
        };
        var controller = new PersonDetectionController(config);
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        controller.ProcessNestEvent(new NestEventPayload("dev1", "camera_person", "ev1", null), t0);
        AssertTrue(controller.IsPopupVisible, "Popup visible at t0");

        DetectionDecision ext = controller.ProcessNestEvent(
            new NestEventPayload("dev1", "camera_motion", "ev2", null),
            t0.AddSeconds(12));
        AssertEqual(DetectionDecision.ExtendPopup, ext, "Continuation motion on active device should extend popup");

        AssertTrue(!controller.CheckPresenceWatchdogExpired(t0.AddSeconds(25)), "Watchdog should not expire at t+25s");
        AssertTrue(controller.IsPopupVisible, "Popup still visible at t+25s");

        AssertTrue(controller.CheckPresenceWatchdogExpired(t0.AddSeconds(33)), "Watchdog should expire at t+33s");
        AssertTrue(!controller.IsPopupVisible, "Popup hidden after watchdog expiry");
    }

    private static void TestMultiCameraRoutingAndSwitching()
    {
        var config = new AppConfig
        {
            PersonAbsenceTimeoutSeconds = 30,
            Cameras = new List<MonitoredCameraConfig>
            {
                new()
                {
                    EntityId = "camera.front_yard",
                    FriendlyName = "Front Yard",
                    DeviceId = "dev_front",
                    PersonEventEntityId = "event.front_yard_motion",
                    Enabled = true
                },
                new()
                {
                    EntityId = "camera.backyard",
                    FriendlyName = "Backyard",
                    DeviceId = "dev_back",
                    PersonEventEntityId = "event.backyard_motion",
                    Enabled = true
                },
                new()
                {
                    EntityId = "camera.garage",
                    FriendlyName = "Garage",
                    DeviceId = "dev_garage",
                    Enabled = false // Unchecked in UI
                }
            }
        };

        var controller = new PersonDetectionController(config);
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        // Disabled camera should be ignored
        var dGarage = controller.ProcessNestEvent(new NestEventPayload("dev_garage", "camera_person", "ev0", null), t0);
        AssertEqual(DetectionDecision.Ignore, dGarage, "Unchecked camera must be ignored");

        // Front Yard detects person via nest_event
        var dFront = controller.ProcessNestEvent(new NestEventPayload("dev_front", "camera_person", "ev1", null), t0);
        AssertEqual(DetectionDecision.ShowPopup, dFront, "Front Yard should open popup");
        AssertEqual("camera.front_yard", controller.ActiveCamera?.EntityId, "Active camera is Front Yard");

        // Backyard detects person 5s later via HA Nest event.backyard_motion entity -> switches active camera feed!
        var dBack = controller.ProcessStateChanged(
            new StateChangedPayload("event.backyard_motion", "old_ts", "new_ts", "camera_person", "dev_back"),
            t0.AddSeconds(5));
        AssertEqual(DetectionDecision.SwitchCameraPopup, dBack, "Backyard person detection should switch popup to Backyard");
        AssertEqual("camera.backyard", controller.ActiveCamera?.EntityId, "Active camera switched to Backyard");

        // Verify upgrade from camera_motion to camera_person on same state timestamp triggers ShowPopup
        controller.DismissManually(t0.AddSeconds(6));
        var dMotionFirst = controller.ProcessStateChanged(
            new StateChangedPayload("event.front_yard_motion", "t1", "t2", "camera_motion", "dev_front", "camera_motion", "nest_1"),
            t0.AddSeconds(30));
        AssertEqual(DetectionDecision.Ignore, dMotionFirst, "Initial camera_motion on event.front_yard_motion should be ignored");

        var dPersonUpgrade = controller.ProcessStateChanged(
            new StateChangedPayload("event.front_yard_motion", "t2", "t2", "camera_person", "dev_front", "camera_motion", "nest_1"),
            t0.AddSeconds(31));
        AssertEqual(DetectionDecision.ShowPopup, dPersonUpgrade, "Upgrade from camera_motion to camera_person must trigger ShowPopup");
    }

    private static void TestPerCameraManualDismissCooldown()
    {
        var config = new AppConfig
        {
            PersonAbsenceTimeoutSeconds = 30,
            ManualDismissCooldownSeconds = 15,
            Cameras = new List<MonitoredCameraConfig>
            {
                new() { EntityId = "camera.front_yard", FriendlyName = "Front Yard", DeviceId = "dev_front", Enabled = true },
                new() { EntityId = "camera.backyard", FriendlyName = "Backyard", DeviceId = "dev_back", Enabled = true }
            }
        };
        var controller = new PersonDetectionController(config);
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        controller.ProcessNestEvent(new NestEventPayload("dev_front", "camera_person", "ev1", null), t0);
        AssertTrue(controller.IsPopupVisible, "Popup visible for Front Yard");

        // User clicks Hide at t+3s while Front Yard is showing
        controller.DismissManually(t0.AddSeconds(3));
        AssertTrue(!controller.IsPopupVisible, "Popup hidden immediately after manual dismiss");

        // Front Yard re-triggers at t+8s (within 15s cooldown) -> ignored
        var frontDuringCooldown = controller.ProcessNestEvent(
            new NestEventPayload("dev_front", "camera_person", "ev2", null),
            t0.AddSeconds(8));
        AssertEqual(DetectionDecision.Ignore, frontDuringCooldown, "Front Yard re-trigger within cooldown must be ignored");

        // Backyard triggers at t+9s -> NOT blocked by Front Yard's cooldown!
        var backDuringFrontCooldown = controller.ProcessNestEvent(
            new NestEventPayload("dev_back", "camera_person", "ev3", null),
            t0.AddSeconds(9));
        AssertEqual(DetectionDecision.ShowPopup, backDuringFrontCooldown, "Backyard must still trigger while Front Yard is on cooldown");
        AssertEqual("camera.backyard", controller.ActiveCamera?.EntityId, "Active camera is Backyard");
    }

    private static void TestSnoozeMode()
    {
        var config = new AppConfig();
        var controller = new PersonDetectionController(config);
        DateTimeOffset t0 = DateTimeOffset.UtcNow;

        controller.ProcessNestEvent(new NestEventPayload("dev1", "camera_person", "ev1", null), t0);
        AssertTrue(controller.IsPopupVisible, "Popup visible before snooze");

        controller.SetSnooze(TimeSpan.FromMinutes(30), t0);
        AssertTrue(!controller.IsPopupVisible, "Snoozing hides active popup");
        AssertTrue(controller.IsSnoozed(t0.AddMinutes(10)), "Still snoozed at +10m");

        var decision = controller.ProcessNestEvent(
            new NestEventPayload("dev1", "camera_person", "ev2", null),
            t0.AddMinutes(10));
        AssertEqual(DetectionDecision.Ignore, decision, "Events ignored while snoozed");

        controller.SetSnooze(null, t0.AddMinutes(11));
        AssertTrue(!controller.IsSnoozed(t0.AddMinutes(11)), "Not snoozed after resume");
    }

    private static void TestHomeAssistantJsonParsing()
    {
        string nestJson = """
        {
          "event_type": "nest_event",
          "data": {
            "device_id": "nest_outdoor_123",
            "type": "camera_person",
            "timestamp": "2026-10-28T21:00:00Z",
            "nest_event_id": " session_abc "
          }
        }
        """;

        using JsonDocument doc1 = JsonDocument.Parse(nestJson);
        NestEventPayload? nestEvt = PersonDetectionController.TryParseNestEventJson(doc1.RootElement);
        AssertTrue(nestEvt != null, "Parsed nest_event should not be null");
        AssertEqual("nest_outdoor_123", nestEvt!.DeviceId, "DeviceId parsed");
        AssertEqual("camera_person", nestEvt.Type, "Type parsed");

        string stateJson = """
        {
          "event_type": "state_changed",
          "data": {
            "entity_id": "event.outdoor_cam_person",
            "old_state": { "state": "2026-10-28T20:00:00.000+00:00" },
            "new_state": {
              "state": "2026-10-28T21:00:00.000+00:00",
              "attributes": {
                "event_type": "camera_person",
                "device_id": "nest_outdoor_123"
              }
            }
          }
        }
        """;

        using JsonDocument doc2 = JsonDocument.Parse(stateJson);
        StateChangedPayload? sc = PersonDetectionController.TryParseStateChangedJson(doc2.RootElement);
        AssertTrue(sc != null, "Parsed state_changed should not be null");
        AssertEqual("event.outdoor_cam_person", sc!.EntityId, "EntityId parsed");
        AssertEqual("camera_person", sc.EventTypeAttribute, "event_type attribute parsed");
    }

    private static void TestUrlNormalization()
    {
        Uri ws1 = HomeAssistantClient.BuildWebSocketUri("http://192.168.1.50:8123/");
        AssertEqual("ws://192.168.1.50:8123/api/websocket", ws1.ToString(), "HTTP -> WS URI");

        Uri ws2 = HomeAssistantClient.BuildWebSocketUri("https://ha.example.com");
        AssertEqual("wss://ha.example.com/api/websocket", ws2.ToString(), "HTTPS -> WSS URI");

        string httpBase = HomeAssistantClient.NormalizeHttpBaseUrl("wss://ha.example.com/api/websocket");
        AssertEqual("https://ha.example.com", httpBase, "WSS -> HTTPS base URL");
    }

    private static void TestMonitorPlacementCalculations()
    {
        var leftMonitor = new MonitorInfo(
            @"\\.\DISPLAY2",
            "Left QHD Monitor",
            false,
            new Rectangle(-2560, 0, 2560, 1440),
            new Rectangle(-2560, 0, 2560, 1400));

        var cfg = new AppConfig
        {
            PositionPreset = PopupPositionPreset.TopRight,
            EdgeMargin = 20,
            Width = 480,
            Height = 270
        };

        Rectangle topRight = MonitorHelper.CalculatePopupBounds(cfg, leftMonitor);
        AssertEqual(-2560 + 2560 - 480 - 20, topRight.X, "TopRight X on negative-offset secondary monitor");
        AssertEqual(20, topRight.Y, "TopRight Y on secondary monitor");

        cfg.PositionPreset = PopupPositionPreset.Custom;
        cfg.CustomX = 99999;
        cfg.CustomY = -500;
        Rectangle clamped = MonitorHelper.CalculatePopupBounds(cfg, leftMonitor);
        AssertEqual(-480, clamped.X, "Clamped X to right edge of secondary monitor");
        AssertEqual(0, clamped.Y, "Clamped Y to top edge of secondary monitor");
    }

    private static void TestWin32StylesAndHotkeys()
    {
        bool parsed = Win32WindowHelper.TryParseHotkey("Ctrl+Shift+X", out uint mods, out uint vk);
        AssertTrue(parsed, "Ctrl+Shift+X should parse");
        AssertTrue((mods & Win32WindowHelper.MOD_CONTROL) != 0, "MOD_CONTROL bit set");
        AssertTrue((mods & Win32WindowHelper.MOD_SHIFT) != 0, "MOD_SHIFT bit set");
        AssertEqual((uint)'X', vk, "Virtual key for X is 0x58");

        var win = new Window
        {
            Width = 320,
            Height = 180,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false
        };

        var helper = new WindowInteropHelper(win);
        IntPtr hwnd = helper.EnsureHandle();
        AssertTrue(hwnd != IntPtr.Zero, "Window handle created");

        Win32WindowHelper.ApplyNonActivatingOverlayStyle(hwnd, clickThrough: true);
        int exStyle = Win32WindowHelper.GetWindowLong(hwnd, Win32WindowHelper.GWL_EXSTYLE);

        AssertTrue((exStyle & Win32WindowHelper.WS_EX_NOACTIVATE) != 0, "WS_EX_NOACTIVATE must be applied");
        AssertTrue((exStyle & Win32WindowHelper.WS_EX_TOOLWINDOW) != 0, "WS_EX_TOOLWINDOW must be applied");
        AssertTrue((exStyle & Win32WindowHelper.WS_EX_TOPMOST) != 0, "WS_EX_TOPMOST must be applied");
        AssertTrue((exStyle & Win32WindowHelper.WS_EX_TRANSPARENT) != 0, "WS_EX_TRANSPARENT must be applied when clickThrough=true");

        win.Close();
    }

    private static void TestRelaxingChimeSynthesis()
    {
        byte[] wav = ChimeSoundService.GenerateRelaxingChimeWav(0.5);
        AssertTrue(wav.Length > 44, "WAV buffer must include header + PCM samples");
        AssertEqual((byte)'R', wav[0], "RIFF header R");
        AssertEqual((byte)'I', wav[1], "RIFF header I");
        AssertEqual((byte)'F', wav[2], "RIFF header F");
        AssertEqual((byte)'F', wav[3], "RIFF header F");

        // Verify zero-click start sample (sample 0 is 0)
        short firstLeft = BitConverter.ToInt16(wav, 44);
        AssertEqual((short)0, firstLeft, "First sample must start at 0 amplitude to prevent speaker click");

        // Verify extended duration (at least 2.5 seconds at 44.1kHz 16-bit stereo)
        const int sampleRate = 44100;
        const int bytesPerSamplePoint = 4; // 16-bit stereo = 2 * 2 bytes
        double durationSeconds = (wav.Length - 44) / (double)(sampleRate * bytesPerSamplePoint);
        AssertTrue(durationSeconds >= 2.5, $"Chime duration must be extended (actual: {durationSeconds:F2}s)");

        // Save preview WAV for verification
        string previewPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "doorbell_preview.wav");
        File.WriteAllBytes(previewPath, wav);
    }
}
