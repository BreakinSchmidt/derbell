using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Derbell.Interop;
using Derbell.Models;
using Derbell.Services;
using Microsoft.Web.WebView2.Core;

namespace Derbell.Views;

public partial class PopupWindow : Window
{
    private const int HotkeyIdDismiss = 9001;

    private readonly HomeAssistantClient _haClient;
    private readonly List<JsonElement> _bufferedLocalCandidates = new();
    private AppConfig _config;
    private MonitoredCameraConfig? _currentCamera;
    private string? _pendingCameraEntityId;
    private string? _activeWebRtcSessionId;
    private string? _activeWebRtcCameraId;
    private DateTimeOffset _lastWebRtcConnectedAt = DateTimeOffset.MinValue;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _webViewInitializing;
    private bool _webViewDomReady;
    private bool _webRtcPlaying;
    private bool _isMuted = true;
    private bool _isPlacementPreviewMode;
    private CancellationTokenSource? _activeSessionCts;

    public event Action? ManualDismissRequested;
    public event Action? VideoStreamStarted;
    public event Action<MonitorInfo, int, int>? PlacementSaved;

    public PopupWindow(AppConfig config, HomeAssistantClient haClient)
    {
        InitializeComponent();
        _config = config;
        _haClient = haClient;
        _isMuted = config.Muted;
        UpdateMuteButtonText();

        _haClient.WebRtcSessionReceived += OnWebRtcSessionReceived;
        _haClient.WebRtcAnswerReceived += OnWebRtcAnswerReceived;
        _haClient.WebRtcRemoteCandidateReceived += OnWebRtcRemoteCandidateReceived;
        _haClient.WebRtcErrorReceived += OnWebRtcErrorReceived;

        Loaded += PopupWindow_Loaded;
    }

    public void EnsureHandleCreatedAndPrewarmed()
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();
        Win32WindowHelper.ApplyNonActivatingOverlayStyle(_hwnd, _config.ClickThroughVideo);
        RegisterConfiguredHotkey();
        _ = InitializeWebViewAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        HwndSource? source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);

        Win32WindowHelper.ApplyNonActivatingOverlayStyle(_hwnd, _config.ClickThroughVideo);
        RegisterConfiguredHotkey();
    }

    private async void PopupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    public void ApplyConfig(AppConfig config)
    {
        bool keepWarmChanged = _config.KeepStreamWarm != config.KeepStreamWarm;
        _config = config;
        _isMuted = config.Muted;
        UpdateMuteButtonText();

        if (_hwnd != IntPtr.Zero)
        {
            Win32WindowHelper.ApplyNonActivatingOverlayStyle(_hwnd, _config.ClickThroughVideo);
            RegisterConfiguredHotkey();
        }

        if (_webViewDomReady)
        {
            SendJsonToWebView(new { type = "set_muted", muted = _isMuted });
            if (keepWarmChanged && !_config.KeepStreamWarm && !IsVisible)
            {
                SendJsonToWebView(new { type = "stop" });
            }
        }
    }

    private void RegisterConfiguredHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;

        Win32WindowHelper.UnregisterHotKey(_hwnd, HotkeyIdDismiss);
        if (Win32WindowHelper.TryParseHotkey(_config.DismissHotkey, out uint mods, out uint vk))
        {
            Win32WindowHelper.RegisterHotKey(_hwnd, HotkeyIdDismiss, mods, vk);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32WindowHelper.WM_MOUSEACTIVATE && !_isPlacementPreviewMode)
        {
            handled = true;
            return new IntPtr(Win32WindowHelper.MA_NOACTIVATE);
        }

        if (msg == Win32WindowHelper.WM_HOTKEY && wParam.ToInt32() == HotkeyIdDismiss)
        {
            handled = true;
            Dispatcher.BeginInvoke(() => ManualDismissRequested?.Invoke());
        }

        return IntPtr.Zero;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewDomReady || _webViewInitializing) return;
        _webViewInitializing = true;

        try
        {
            string userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Derbell",
                "WebView2");
            Directory.CreateDirectory(userDataDir);

            var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userDataDir, options);
            await StreamWebView.EnsureCoreWebView2Async(env);

            StreamWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 9, 13, 22);
            StreamWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            StreamWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            StreamWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            StreamWebView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;

            var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                StreamWebView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
                navTcs.TrySetResult(true);
            }
            StreamWebView.CoreWebView2.NavigationCompleted += OnNavCompleted;

            StreamWebView.NavigateToString(GetEmbeddedWebRtcPlayerHtml());
            await navTcs.Task;

            _webViewDomReady = true;
            PlaceholderPanel.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrWhiteSpace(_pendingCameraEntityId))
            {
                string camId = _pendingCameraEntityId;
                _pendingCameraEntityId = null;
                _ = BeginWebRtcStreamAsync(camId);
            }
        }
        catch (Exception ex)
        {
            _webViewInitializing = false;
            StatusTitleText.Text = "WEBVIEW2 ERROR";
            StatusSubText.Text = ex.Message;
        }
    }

    public void ShowNonActivatingPopup(MonitoredCameraConfig camera, string reason)
    {
        Dispatcher.Invoke(() =>
        {
            bool switchingCameras = _currentCamera != null &&
                !string.Equals(_currentCamera.EntityId, camera.EntityId, StringComparison.OrdinalIgnoreCase);

            _currentCamera = camera;
            _isPlacementPreviewMode = false;
            PreviewOverlay.Visibility = Visibility.Collapsed;
            StreamWebView.Visibility = Visibility.Visible;

            string camName = string.IsNullOrWhiteSpace(camera.FriendlyName)
                ? (string.IsNullOrWhiteSpace(camera.EntityId) ? "Nest Cam" : camera.EntityId)
                : camera.FriendlyName;

            CameraTitleText.Text = $"{camName} — Person Detected";

            if (switchingCameras)
            {
                _webRtcPlaying = false;
            }

            PositionAndShowTopmostNoActivate();
            StartStreamSession(camera.EntityId, camName);
        });
    }

    public void ShowPlacementPreview(AppConfig previewConfig)
    {
        Dispatcher.Invoke(() =>
        {
            _config = previewConfig;
            _isPlacementPreviewMode = true;
            StreamWebView.Visibility = Visibility.Hidden;
            PreviewOverlay.Visibility = Visibility.Visible;
            CameraTitleText.Text = "Placement Preview — Drag to Spot";
            StreamModeBadge.Text = "PREVIEW";

            PositionAndShowTopmostNoActivate();
        });
    }

    private void PositionAndShowTopmostNoActivate()
    {
        var helper = new WindowInteropHelper(this);
        _hwnd = helper.EnsureHandle();

        MonitorInfo monitor = MonitorHelper.GetTargetMonitor(_config.TargetMonitorDeviceName);
        System.Drawing.Rectangle bounds = MonitorHelper.CalculatePopupBounds(_config, monitor);

        Win32WindowHelper.ApplyNonActivatingOverlayStyle(
            _hwnd,
            !_isPlacementPreviewMode && _config.ClickThroughVideo);

        if (!IsVisible)
        {
            Show();
        }

        Win32WindowHelper.ShowTopmostWithoutActivation(
            _hwnd,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
    }

    public void HideNonActivatingPopup()
    {
        Dispatcher.Invoke(() =>
        {
            _isPlacementPreviewMode = false;
            PreviewOverlay.Visibility = Visibility.Collapsed;

            _activeSessionCts?.Cancel();
            _activeSessionCts?.Dispose();
            _activeSessionCts = null;

            if (_webViewDomReady && !_config.KeepStreamWarm)
            {
                _webRtcPlaying = false;
                SendJsonToWebView(new { type = "stop" });
            }

            if (_hwnd != IntPtr.Zero)
            {
                Win32WindowHelper.HideWithoutActivation(_hwnd);
            }
            Hide();
        });
    }

    private void StartStreamSession(string cameraEntityId, string friendlyName)
    {
        _activeSessionCts?.Cancel();
        _activeSessionCts?.Dispose();
        _activeSessionCts = new CancellationTokenSource();

        if (string.IsNullOrWhiteSpace(cameraEntityId))
        {
            StreamModeBadge.Text = "NO CAMERA";
            if (_webViewDomReady)
            {
                SendJsonToWebView(new
                {
                    type = "status",
                    title = "NO CAMERA SELECTED",
                    subtitle = "Open Derbell Settings, click Connect & Discover Cameras, and check your Nest camera."
                });
            }
            return;
        }

        bool canReuseWarmStream =
            _config.KeepStreamWarm &&
            _webRtcPlaying &&
            string.Equals(_activeWebRtcCameraId, cameraEntityId, StringComparison.OrdinalIgnoreCase) &&
            (DateTimeOffset.UtcNow - _lastWebRtcConnectedAt).TotalSeconds < 60;

        if (canReuseWarmStream)
        {
            StreamModeBadge.Text = "WEBRTC LIVE";
            VideoStreamStarted?.Invoke();
            return;
        }

        _webRtcPlaying = false;
        _activeWebRtcCameraId = cameraEntityId;
        StreamModeBadge.Text = "CONNECTING";

        if (!_webViewDomReady)
        {
            _pendingCameraEntityId = cameraEntityId;
            _ = InitializeWebViewAsync();
            return;
        }

        _ = BeginWebRtcStreamAsync(cameraEntityId);
    }

    private async Task BeginWebRtcStreamAsync(string cameraEntityId)
    {
        CancellationToken ct = _activeSessionCts?.Token ?? CancellationToken.None;
        _activeWebRtcSessionId = null;
        lock (_bufferedLocalCandidates)
        {
            _bufferedLocalCandidates.Clear();
        }

        SendJsonToWebView(new
        {
            type = "status",
            title = "CONNECTING LIVE STREAM...",
            subtitle = $"Negotiating WebRTC with {cameraEntityId}..."
        });

        string? clientConfigJson = await _haClient.GetWebRtcClientConfigJsonAsync(cameraEntityId, ct);
        if (ct.IsCancellationRequested) return;

        await Dispatcher.InvokeAsync(() =>
        {
            SendJsonToWebView(new
            {
                type = "start_webrtc",
                muted = _isMuted,
                clientConfigJson
            });
        });
    }

    private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(e.WebMessageAsJson);
            JsonElement root = doc.RootElement;
            string? msgType = root.TryGetProperty("type", out JsonElement t) ? t.GetString() : null;

            string? activeEntityId = _currentCamera?.EntityId;

            if (string.Equals(msgType, "webrtc_offer", StringComparison.OrdinalIgnoreCase))
            {
                string? sdpOffer = root.TryGetProperty("sdp", out JsonElement sdpProp) ? sdpProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(sdpOffer) && !string.IsNullOrWhiteSpace(activeEntityId))
                {
                    StreamModeBadge.Text = "HANDSHAKE";
                    CancellationToken ct = _activeSessionCts?.Token ?? CancellationToken.None;
                    await _haClient.StartWebRtcOfferAsync(activeEntityId, sdpOffer, ct);
                }
            }
            else if (string.Equals(msgType, "webrtc_local_candidate", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("candidate", out JsonElement candEl) && !string.IsNullOrWhiteSpace(activeEntityId))
                {
                    JsonElement clonedCand = candEl.Clone();
                    string? sessionId = _activeWebRtcSessionId;

                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        CancellationToken ct = _activeSessionCts?.Token ?? CancellationToken.None;
                        await _haClient.SendWebRtcCandidateAsync(activeEntityId, sessionId, clonedCand, ct);
                    }
                    else
                    {
                        lock (_bufferedLocalCandidates)
                        {
                            _bufferedLocalCandidates.Add(clonedCand);
                        }
                    }
                }
            }
            else if (string.Equals(msgType, "video_playing", StringComparison.OrdinalIgnoreCase))
            {
                _webRtcPlaying = true;
                _lastWebRtcConnectedAt = DateTimeOffset.UtcNow;
                StreamModeBadge.Text = "WEBRTC LIVE";
                VideoStreamStarted?.Invoke();
            }
            else if (string.Equals(msgType, "webrtc_error", StringComparison.OrdinalIgnoreCase))
            {
                _webRtcPlaying = false;
                StreamModeBadge.Text = "STREAM RETRY";
            }
        }
        catch
        {
            // Ignore unexpected WebView messages
        }
    }

    private async void OnWebRtcSessionReceived(string sessionId)
    {
        _activeWebRtcSessionId = sessionId;
        string? activeEntityId = _currentCamera?.EntityId;
        if (string.IsNullOrWhiteSpace(activeEntityId)) return;

        List<JsonElement> toFlush;
        lock (_bufferedLocalCandidates)
        {
            toFlush = new List<JsonElement>(_bufferedLocalCandidates);
            _bufferedLocalCandidates.Clear();
        }

        CancellationToken ct = _activeSessionCts?.Token ?? CancellationToken.None;
        foreach (JsonElement cand in toFlush)
        {
            await _haClient.SendWebRtcCandidateAsync(activeEntityId, sessionId, cand, ct);
        }
    }

    private void OnWebRtcAnswerReceived(string answerSdp)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_webViewDomReady) return;
            SendJsonToWebView(new
            {
                type = "webrtc_answer",
                sdp = answerSdp
            });
        });
    }

    private void OnWebRtcRemoteCandidateReceived(string candidateJson)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_webViewDomReady) return;
            SendJsonToWebView(new
            {
                type = "webrtc_candidate",
                candidateJson
            });
        });
    }

    private void OnWebRtcErrorReceived(string errorMessage)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_webViewDomReady) return;
            StreamModeBadge.Text = "ERROR";
            SendJsonToWebView(new
            {
                type = "status",
                title = "STREAM CONNECTION ISSUE",
                subtitle = errorMessage
            });
        });
    }

    private void SendJsonToWebView(object message)
    {
        if (!_webViewDomReady || StreamWebView.CoreWebView2 == null) return;
        string json = JsonSerializer.Serialize(message);
        StreamWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void MuteToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        UpdateMuteButtonText();
        SendJsonToWebView(new { type = "set_muted", muted = _isMuted });
    }

    private void UpdateMuteButtonText()
    {
        MuteToggleButton.Content = _isMuted ? "🔇 Muted" : "🔊 Audio";
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlacementPreviewMode)
        {
            HideNonActivatingPopup();
            return;
        }

        ManualDismissRequested?.Invoke();
    }

    private void PreviewOverlay_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isPlacementPreviewMode && e.ButtonState == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void SavePreviewPlacement_Click(object sender, RoutedEventArgs e)
    {
        int physX = (int)Left;
        int physY = (int)Top;
        if (_hwnd != IntPtr.Zero && Win32WindowHelper.GetWindowRect(_hwnd, out Win32WindowHelper.RECT rect))
        {
            physX = rect.Left;
            physY = rect.Top;
        }

        System.Drawing.Point topLeft = new(physX + 10, physY + 10);
        var screen = System.Windows.Forms.Screen.FromPoint(topLeft);
        IReadOnlyList<MonitorInfo> monitors = MonitorHelper.GetMonitors();
        MonitorInfo targetMon = MonitorHelper.GetTargetMonitor(screen.DeviceName, monitors);

        int relX = physX - targetMon.Bounds.Left;
        int relY = physY - targetMon.Bounds.Top;

        HideNonActivatingPopup();
        PlacementSaved?.Invoke(targetMon, relX, relY);
    }

    private void CancelPreviewPlacement_Click(object sender, RoutedEventArgs e)
    {
        HideNonActivatingPopup();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            Win32WindowHelper.UnregisterHotKey(_hwnd, HotkeyIdDismiss);
        }
        _haClient.WebRtcSessionReceived -= OnWebRtcSessionReceived;
        _haClient.WebRtcAnswerReceived -= OnWebRtcAnswerReceived;
        _haClient.WebRtcRemoteCandidateReceived -= OnWebRtcRemoteCandidateReceived;
        _haClient.WebRtcErrorReceived -= OnWebRtcErrorReceived;
        base.OnClosed(e);
    }

    private static string GetEmbeddedWebRtcPlayerHtml() => """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8" />
<style>
  html, body {
    margin: 0;
    padding: 0;
    width: 100%;
    height: 100%;
    background: #090D16;
    overflow: hidden;
    user-select: none;
    font-family: 'Segoe UI', sans-serif;
  }
  video {
    width: 100%;
    height: 100%;
    object-fit: contain;
    background: #090D16;
  }
  #statusOverlay {
    position: absolute;
    inset: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    background: rgba(9, 13, 22, 0.85);
    color: #F8FAFC;
    text-align: center;
    padding: 16px;
    pointer-events: none;
    transition: opacity 0.2s ease;
  }
  #statusOverlay.hidden {
    opacity: 0;
  }
  .spinner {
    width: 28px;
    height: 28px;
    border: 3px solid #1E293B;
    border-top-color: #38BDF8;
    border-radius: 50%;
    animation: spin 0.9s linear infinite;
    margin-bottom: 10px;
  }
  @keyframes spin {
    to { transform: rotate(360deg); }
  }
  #statusTitle {
    color: #38BDF8;
    font-size: 13.5px;
    font-weight: 700;
    letter-spacing: 0.4px;
  }
  #statusSub {
    color: #94A3B8;
    font-size: 11.5px;
    margin-top: 5px;
    max-width: 90%;
  }
</style>
</head>
<body>
  <video id="camVideo" autoplay playsinline muted></video>
  <div id="statusOverlay">
    <div class="spinner" id="spinnerEl"></div>
    <div id="statusTitle">CONNECTING TO NEST CAMERA...</div>
    <div id="statusSub">Initializing WebRTC stream via Home Assistant...</div>
  </div>
  <script>
    let pc = null;
    let remoteDescSet = false;
    let queuedRemoteCandidates = [];
    const videoEl = document.getElementById('camVideo');
    const overlayEl = document.getElementById('statusOverlay');
    const titleEl = document.getElementById('statusTitle');
    const subEl = document.getElementById('statusSub');

    function showStatus(title, sub) {
      titleEl.textContent = title;
      subEl.textContent = sub || '';
      overlayEl.classList.remove('hidden');
    }

    videoEl.addEventListener('playing', () => {
      overlayEl.classList.add('hidden');
      window.chrome.webview.postMessage({ type: 'video_playing' });
    });

    videoEl.addEventListener('loadeddata', () => {
      if (videoEl.readyState >= 2) {
        overlayEl.classList.add('hidden');
        window.chrome.webview.postMessage({ type: 'video_playing' });
      }
    });

    function stopStream() {
      remoteDescSet = false;
      queuedRemoteCandidates = [];
      if (pc) {
        try { pc.close(); } catch {}
        pc = null;
      }
      if (videoEl.srcObject) {
        videoEl.srcObject = null;
      }
    }

    async function startWebRtc(muted, clientConfigJson) {
      stopStream();
      videoEl.muted = !!muted;
      showStatus('NEGOTIATING WEBRTC...', 'Waking up Google Nest camera stream (takes ~2-4s)...');

      try {
        let rtcConfig = {
          iceServers: [{ urls: 'stun:stun.l.google.com:19302' }]
        };
        let dataChannelName = 'dataSendChannel';

        if (clientConfigJson) {
          try {
            const parsed = JSON.parse(clientConfigJson);
            if (parsed.configuration && parsed.configuration.iceServers) {
              rtcConfig = parsed.configuration;
            }
            if (parsed.dataChannel) {
              dataChannelName = parsed.dataChannel;
            }
          } catch {}
        }

        pc = new RTCPeerConnection(rtcConfig);

        pc.onicecandidate = (event) => {
          if (event.candidate) {
            window.chrome.webview.postMessage({
              type: 'webrtc_local_candidate',
              candidate: event.candidate.toJSON()
            });
          }
        };

        pc.ontrack = (event) => {
          if (event.streams && event.streams[0]) {
            if (videoEl.srcObject !== event.streams[0]) {
              videoEl.srcObject = event.streams[0];
            }
          } else {
            if (!videoEl.srcObject) {
              videoEl.srcObject = new MediaStream();
            }
            videoEl.srcObject.addTrack(event.track);
          }
          videoEl.play().catch(() => {});
        };

        pc.onconnectionstatechange = () => {
          if (!pc) return;
          if (pc.connectionState === 'connected') {
            showStatus('STREAM CONNECTED', 'Waiting for first video keyframe...');
          } else if (pc.connectionState === 'failed') {
            showStatus('WEBRTC CONNECTION FAILED', 'ICE connectivity failed. Check network/firewall.');
            window.chrome.webview.postMessage({ type: 'webrtc_error' });
          }
        };

        // Google Nest SDM WebRTC expects audio recvonly, video recvonly, and a data channel
        pc.addTransceiver('audio', { direction: 'recvonly' });
        pc.addTransceiver('video', { direction: 'recvonly' });
        pc.createDataChannel(dataChannelName);

        const offer = await pc.createOffer();
        await pc.setLocalDescription(offer);

        window.chrome.webview.postMessage({
          type: 'webrtc_offer',
          sdp: pc.localDescription.sdp
        });
      } catch (err) {
        showStatus('WEBRTC INIT ERROR', String(err));
        window.chrome.webview.postMessage({ type: 'webrtc_error', error: String(err) });
      }
    }

    window.chrome.webview.addEventListener('message', async (event) => {
      const msg = event.data;
      if (!msg || !msg.type) return;

      if (msg.type === 'status') {
        showStatus(msg.title || 'CONNECTING...', msg.subtitle || '');
      } else if (msg.type === 'start_webrtc') {
        await startWebRtc(msg.muted, msg.clientConfigJson);
      } else if (msg.type === 'webrtc_answer' && pc && msg.sdp) {
        try {
          showStatus('SDP ANSWER RECEIVED', 'Establishing peer media path...');
          await pc.setRemoteDescription(new RTCSessionDescription({
            type: 'answer',
            sdp: msg.sdp
          }));
          remoteDescSet = true;
          for (const cand of queuedRemoteCandidates) {
            try { await pc.addIceCandidate(new RTCIceCandidate(cand)); } catch {}
          }
          queuedRemoteCandidates = [];
        } catch (err) {
          showStatus('SDP ANSWER ERROR', String(err));
          window.chrome.webview.postMessage({ type: 'webrtc_error', error: String(err) });
        }
      } else if (msg.type === 'webrtc_candidate' && pc && msg.candidateJson) {
        try {
          const candObj = JSON.parse(msg.candidateJson);
          if (remoteDescSet) {
            await pc.addIceCandidate(new RTCIceCandidate(candObj));
          } else {
            queuedRemoteCandidates.push(candObj);
          }
        } catch {}
      } else if (msg.type === 'set_muted') {
        videoEl.muted = !!msg.muted;
      } else if (msg.type === 'stop') {
        stopStream();
      }
    });
  </script>
</body>
</html>
""";
}
