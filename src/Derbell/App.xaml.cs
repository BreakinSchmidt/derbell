using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Derbell.Models;
using Derbell.Services;
using Derbell.Views;

namespace Derbell;

public partial class App : System.Windows.Application
{
    private const string ShutdownSignalName = @"Local\Derbell_Shutdown_Signal";
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _shutdownSignal;
    private AppConfig _config = new();
    private HomeAssistantClient? _haClient;
    private PersonDetectionController? _detectionController;
    private PopupWindow? _popupWindow;
    private TrayIconService? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _watchdogTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool isSelfTest = e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
        bool isShutdown = e.Args.Any(a => string.Equals(a, "--shutdown", StringComparison.OrdinalIgnoreCase));

        if (isShutdown)
        {
            if (EventWaitHandle.TryOpenExisting(ShutdownSignalName, out EventWaitHandle? existingSignal))
            {
                existingSignal.Set();
                existingSignal.Dispose();
            }
            Shutdown(0);
            return;
        }

        if (!isSelfTest)
        {
            _singleInstanceMutex = new Mutex(true, @"Local\Derbell_SingleInstance_Mutex", out bool createdNew);
            if (!createdNew)
            {
                Shutdown(0);
                return;
            }

            _shutdownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownSignalName);
            ThreadPool.RegisterWaitForSingleObject(
                _shutdownSignal,
                (_, _) => Dispatcher.BeginInvoke(() => Shutdown(0)),
                null,
                -1,
                executeOnlyOnce: true);
        }

        _config = AppConfig.Load();
        _haClient = new HomeAssistantClient(_config);
        _detectionController = new PersonDetectionController(_config);
        _popupWindow = new PopupWindow(_config, _haClient);
        _popupWindow.EnsureHandleCreatedAndPrewarmed();

        _trayIcon = new TrayIconService(_config);

        _haClient.ConnectionStateChanged += (state, detail) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                bool snoozed = _detectionController?.IsSnoozed(DateTimeOffset.UtcNow) == true;
                _trayIcon?.UpdateVisualState(state, detail, snoozed);
            });
        };

        _haClient.DiscoveredCamerasUpdated += discovered =>
        {
            _detectionController?.SyncDiscoveredCameraMetadata(discovered);
        };

        _haClient.NestEventReceived += evt =>
        {
            _detectionController?.ProcessNestEvent(evt, DateTimeOffset.UtcNow);
        };

        _haClient.StateChangedReceived += stateChanged =>
        {
            _detectionController?.ProcessStateChanged(stateChanged, DateTimeOffset.UtcNow);
        };

        // Wire PersonDetectionController -> PopupWindow
        _detectionController.ShowPopupRequested += (camera, reason) =>
        {
            if (!isSelfTest && _config.PlayAlertChime)
            {
                ChimeSoundService.PlayRelaxingChime(_config.AlertChimeVolumePercent);
            }
            _popupWindow?.ShowNonActivatingPopup(camera, reason);
        };

        _detectionController.HidePopupRequested += _ =>
        {
            _popupWindow?.HideNonActivatingPopup();
        };

        _popupWindow.ManualDismissRequested += () =>
        {
            _detectionController?.DismissManually(DateTimeOffset.UtcNow);
        };

        _popupWindow.VideoStreamStarted += () =>
        {
            _detectionController?.NotifyVideoStreamStarted(DateTimeOffset.UtcNow);
        };

        // Wire Tray Icon menu actions
        _trayIcon.ShowCameraRequested += specificCamera =>
        {
            _detectionController?.TriggerManualShow(DateTimeOffset.UtcNow, specificCamera);
        };

        _trayIcon.HideCameraRequested += () =>
        {
            _detectionController?.DismissManually(DateTimeOffset.UtcNow);
        };

        _trayIcon.SnoozeRequested += duration =>
        {
            if (_detectionController == null || _haClient == null) return;
            _detectionController.SetSnooze(duration, DateTimeOffset.UtcNow);
            bool snoozed = _detectionController.IsSnoozed(DateTimeOffset.UtcNow);
            _trayIcon.UpdateVisualState(_haClient.ConnectionState, _haClient.LastError, snoozed);
        };

        _trayIcon.ToggleRunAtStartupRequested += enabled =>
        {
            _config.RunAtStartup = enabled;
            _config.Save();
        };

        _trayIcon.OpenSettingsRequested += OpenSettingsWindow;
        _trayIcon.ExitRequested += () => Shutdown(0);

        // Start 1-second presence watchdog timer
        _watchdogTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _watchdogTimer.Tick += (_, _) =>
        {
            if (_detectionController == null || _haClient == null || _trayIcon == null) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _detectionController.CheckPresenceWatchdogExpired(now);
            _trayIcon.UpdateVisualState(_haClient.ConnectionState, _haClient.LastError, _detectionController.IsSnoozed(now));
        };
        _watchdogTimer.Start();

        if (isSelfTest)
        {
            RunSelfTestAndExit();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_config.AccessToken))
        {
            _haClient.Start();
        }
        else
        {
            OpenSettingsWindow();
        }
    }

    private void RunSelfTestAndExit()
    {
        _detectionController?.TriggerManualShow(DateTimeOffset.UtcNow);
        _detectionController?.DismissManually(DateTimeOffset.UtcNow);
        Shutdown(0);
    }

    private void OpenSettingsWindow()
    {
        if (_haClient == null || _popupWindow == null) return;

        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_config, _haClient, _popupWindow);
        _settingsWindow.ConfigSaved += updatedConfig =>
        {
            _config = updatedConfig;
            _detectionController?.UpdateConfig(_config);
            _popupWindow?.ApplyConfig(_config);
            _trayIcon?.SyncConfig(_config);
            _haClient?.UpdateConfigAndReconnect(_config);
        };
        _settingsWindow.TestPopupRequested += tempConfig =>
        {
            _config = tempConfig;
            _haClient?.UpdateConfigAndReconnect(tempConfig);
            _popupWindow?.ApplyConfig(tempConfig);
            _detectionController?.UpdateConfig(tempConfig);
            _detectionController?.TriggerManualShow(DateTimeOffset.UtcNow);
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdogTimer?.Stop();
        _trayIcon?.Dispose();
        _haClient?.Dispose();
        _popupWindow?.Close();

        if (_singleInstanceMutex != null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
            }
            catch
            {
                // Ignore mutex release on exit
            }
        }

        base.OnExit(e);
    }
}
