using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Derbell.Models;

namespace Derbell.Services;

public enum HaConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    AuthFailed
}

public sealed record HaCameraDiscoveryItem(
    string EntityId,
    string FriendlyName,
    string DeviceId,
    string PersonEventEntityId,
    string InitialEventStateToken = "")
{
    public string DisplayText => string.IsNullOrWhiteSpace(FriendlyName)
        ? EntityId
        : $"{FriendlyName} ({EntityId})";
}

public sealed class HomeAssistantClient : IDisposable
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRequests = new();
    private readonly ConcurrentDictionary<int, Action<JsonElement>> _eventSubscriptions = new();

    private AppConfig _config;
    private CancellationTokenSource? _loopCts;
    private ClientWebSocket? _webSocket;
    private int _nextMessageId = 1;
    private int _connectionGeneration;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public HaConnectionState ConnectionState { get; private set; } = HaConnectionState.Disconnected;
    public string? LastError { get; private set; }

    public event Action<HaConnectionState, string?>? ConnectionStateChanged;
    public event Action<NestEventPayload>? NestEventReceived;
    public event Action<StateChangedPayload>? StateChangedReceived;
    public event Action<string>? WebRtcSessionReceived;
    public event Action<string>? WebRtcAnswerReceived;
    public event Action<string>? WebRtcRemoteCandidateReceived;
    public event Action<string>? WebRtcErrorReceived;
    public event Action<IReadOnlyList<HaCameraDiscoveryItem>>? DiscoveredCamerasUpdated;

    public HomeAssistantClient(AppConfig config)
    {
        _config = config;
    }

    public void UpdateConfigAndReconnect(AppConfig config)
    {
        bool credentialsSame =
            string.Equals(_config.HomeAssistantUrl.Trim(), config.HomeAssistantUrl.Trim(), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_config.AccessToken.Trim(), config.AccessToken.Trim(), StringComparison.Ordinal);

        _config = config;

        if (credentialsSame && ConnectionState == HaConnectionState.Connected && _webSocket?.State == WebSocketState.Open)
        {
            return;
        }

        RestartConnection();
    }

    public void Start()
    {
        if (ConnectionState == HaConnectionState.Connected && _webSocket?.State == WebSocketState.Open)
        {
            return;
        }
        RestartConnection();
    }

    public async Task<bool> EnsureConnectedAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (ConnectionState == HaConnectionState.Connected && _webSocket?.State == WebSocketState.Open)
        {
            return true;
        }

        if (ConnectionState == HaConnectionState.Disconnected)
        {
            Start();
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (ConnectionState == HaConnectionState.Connected && _webSocket?.State == WebSocketState.Open)
            {
                return true;
            }
            if (ConnectionState == HaConnectionState.AuthFailed)
            {
                return false;
            }
            await Task.Delay(100, ct);
        }

        return ConnectionState == HaConnectionState.Connected && _webSocket?.State == WebSocketState.Open;
    }

    public void RestartConnection()
    {
        Stop();
        if (string.IsNullOrWhiteSpace(_config.HomeAssistantUrl) ||
            string.IsNullOrWhiteSpace(_config.AccessToken))
        {
            SetState(HaConnectionState.Disconnected, "Home Assistant URL or Access Token not configured.");
            return;
        }

        int gen = Interlocked.Increment(ref _connectionGeneration);
        _loopCts = new CancellationTokenSource();
        CancellationToken token = _loopCts.Token;
        _ = Task.Run(() => RunConnectionLoopAsync(gen, token), token);
        _ = Task.Run(() => RunEventEntityPollingLoopAsync(gen, token), token);
    }

    public void Stop()
    {
        try
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
        }
        catch
        {
            // Ignore disposal races
        }
        finally
        {
            _loopCts = null;
        }
    }

    private void SetState(HaConnectionState state, string? error = null)
    {
        ConnectionState = state;
        LastError = error;
        ConnectionStateChanged?.Invoke(state, error);
    }

    public static Uri BuildWebSocketUri(string baseUrl)
    {
        string trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "wss://" + trimmed["https://".Length..];
        }
        else if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed["http://".Length..];
        }
        else if (!trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "ws://" + trimmed;
        }

        if (!trimmed.EndsWith("/api/websocket", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/api/websocket";
        }

        return new Uri(trimmed);
    }

    public static string NormalizeHttpBaseUrl(string baseUrl)
    {
        string trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "https://" + trimmed["wss://".Length..];
        }
        else if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed["ws://".Length..];
        }
        else if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                 !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }

        if (trimmed.EndsWith("/api/websocket", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/api/websocket".Length];
        }

        return trimmed;
    }

    private async Task RunConnectionLoopAsync(int generation, CancellationToken ct)
    {
        int retryDelaySeconds = 2;

        while (!ct.IsCancellationRequested && generation == Volatile.Read(ref _connectionGeneration))
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _webSocket = ws;

            try
            {
                SetState(HaConnectionState.Connecting);
                Uri wsUri = BuildWebSocketUri(_config.HomeAssistantUrl);
                await ws.ConnectAsync(wsUri, ct);

                bool authenticated = await PerformAuthHandshakeAsync(ws, ct);
                if (!authenticated)
                {
                    SetState(HaConnectionState.AuthFailed, "Invalid Home Assistant Long-Lived Access Token.");
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    continue;
                }

                retryDelaySeconds = 2;
                SetState(HaConnectionState.Connected);

                // Subscribe to nest_event and state_changed
                await SubscribeEventsAsync("nest_event", HandleEventEnvelope, ct);
                await SubscribeEventsAsync("state_changed", HandleEventEnvelope, ct);

                // Auto-resolve camera DeviceIds and event.<camera>_motion entities in the background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(250, ct);
                        IReadOnlyList<HaCameraDiscoveryItem> cams = await GetDiscoveredCamerasAsync(ct);
                        DiscoveredCamerasUpdated?.Invoke(cams);
                    }
                    catch
                    {
                        // Non-fatal background metadata sync
                    }
                }, ct);

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (generation == Volatile.Read(ref _connectionGeneration))
                {
                    FailPendingRequests(ex);
                    SetState(HaConnectionState.Disconnected, ex.Message);
                }
            }

            if (!ct.IsCancellationRequested && generation == Volatile.Read(ref _connectionGeneration))
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), ct);
                    retryDelaySeconds = Math.Min(30, retryDelaySeconds * 2);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Lightweight fallback poller for each camera's Nest event entity (e.g. event.front_porch_camera_motion)
    /// so person detections are guaranteed to trigger even if WebSocket event-bus subscriptions are restricted for a non-admin token.
    /// </summary>
    private async Task RunEventEntityPollingLoopAsync(int generation, CancellationToken ct)
    {
        var seededEntities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (!ct.IsCancellationRequested && generation == Volatile.Read(ref _connectionGeneration))
        {
            try
            {
                if (ConnectionState == HaConnectionState.Connected)
                {
                    IReadOnlyList<MonitoredCameraConfig> enabledCams = _config.GetEnabledCameras();
                    string baseUrl = NormalizeHttpBaseUrl(_config.HomeAssistantUrl);

                    foreach (MonitoredCameraConfig cam in enabledCams)
                    {
                        string eventEntityId = !string.IsNullOrWhiteSpace(cam.PersonEventEntityId)
                            ? cam.PersonEventEntityId
                            : DeriveDefaultNestMotionEventEntityId(cam.EntityId);

                        if (string.IsNullOrWhiteSpace(eventEntityId)) continue;

                        using var req = new HttpRequestMessage(
                            HttpMethod.Get,
                            $"{baseUrl}/api/states/{Uri.EscapeDataString(eventEntityId)}");
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AccessToken.Trim());

                        using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct);
                        if (!resp.IsSuccessStatusCode) continue;

                        string json = await resp.Content.ReadAsStringAsync(ct);
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        string? stateVal = root.TryGetProperty("state", out JsonElement stProp) ? stProp.GetString() : null;
                        if (string.IsNullOrWhiteSpace(stateVal) ||
                            string.Equals(stateVal, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(stateVal, "unknown", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? eventType = null;
                        string? nestEventId = null;
                        if (root.TryGetProperty("attributes", out JsonElement attrs) && attrs.ValueKind == JsonValueKind.Object)
                        {
                            if (attrs.TryGetProperty("event_type", out JsonElement etProp))
                                eventType = etProp.GetString();
                            if (attrs.TryGetProperty("nest_event_id", out JsonElement nidProp))
                                nestEventId = nidProp.GetString();
                        }

                        if (seededEntities.Add(eventEntityId))
                        {
                            // First poll after launch: seed baseline token in controller without triggering popup
                            DiscoveredCamerasUpdated?.Invoke(new[]
                            {
                                new HaCameraDiscoveryItem(
                                    cam.EntityId,
                                    cam.FriendlyName,
                                    cam.DeviceId,
                                    eventEntityId,
                                    $"{stateVal}|{eventType}|{nestEventId}")
                            });
                            continue;
                        }

                        var payload = new StateChangedPayload(
                            eventEntityId,
                            OldState: null,
                            NewState: stateVal,
                            EventTypeAttribute: eventType,
                            DeviceIdAttribute: cam.DeviceId,
                            OldEventTypeAttribute: null,
                            NestEventIdAttribute: nestEventId);

                        StateChangedReceived?.Invoke(payload);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignore transient poll errors
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static string DeriveDefaultNestMotionEventEntityId(string cameraEntityId)
    {
        if (string.IsNullOrWhiteSpace(cameraEntityId) ||
            !cameraEntityId.StartsWith("camera.", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string slug = cameraEntityId["camera.".Length..];
        return $"event.{slug}_motion";
    }

    private async Task<bool> PerformAuthHandshakeAsync(ClientWebSocket ws, CancellationToken ct)
    {
        string firstMsg = await ReceiveFullTextMessageAsync(ws, ct);
        using (JsonDocument doc1 = JsonDocument.Parse(firstMsg))
        {
            string? type1 = doc1.RootElement.TryGetProperty("type", out JsonElement t1) ? t1.GetString() : null;
            if (!string.Equals(type1, "auth_required", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unexpected initial WebSocket message: {type1}");
            }
        }

        string authPayload = JsonSerializer.Serialize(new
        {
            type = "auth",
            access_token = _config.AccessToken.Trim()
        });
        await SendRawJsonAsync(ws, authPayload, ct);

        string secondMsg = await ReceiveFullTextMessageAsync(ws, ct);
        using JsonDocument doc2 = JsonDocument.Parse(secondMsg);
        string? type2 = doc2.RootElement.TryGetProperty("type", out JsonElement t2) ? t2.GetString() : null;

        return string.Equals(type2, "auth_ok", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SubscribeEventsAsync(string eventType, Action<JsonElement> callback, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextMessageId);
        _eventSubscriptions[id] = callback;

        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "subscribe_events",
            event_type = eventType
        });

        if (_webSocket != null)
        {
            await SendRawJsonAsync(_webSocket, payload, ct);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string raw = await ReceiveFullTextMessageAsync(ws, ct);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement.Clone();

            string? msgType = root.TryGetProperty("type", out JsonElement typeProp)
                ? typeProp.GetString()
                : null;

            int msgId = root.TryGetProperty("id", out JsonElement idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : -1;

            if (string.Equals(msgType, "event", StringComparison.OrdinalIgnoreCase))
            {
                if (msgId > 0 && _eventSubscriptions.TryGetValue(msgId, out Action<JsonElement>? handler))
                {
                    handler(root);
                }
            }
            else if (string.Equals(msgType, "result", StringComparison.OrdinalIgnoreCase))
            {
                if (msgId > 0 && _pendingRequests.TryRemove(msgId, out TaskCompletionSource<JsonElement>? tcs))
                {
                    tcs.TrySetResult(root);
                }
            }
        }
    }

    private void HandleEventEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("event", out JsonElement evt))
            return;

        string? eventType = evt.TryGetProperty("event_type", out JsonElement etProp)
            ? etProp.GetString()
            : null;

        if (string.Equals(eventType, "nest_event", StringComparison.OrdinalIgnoreCase))
        {
            NestEventPayload? parsed = PersonDetectionController.TryParseNestEventJson(evt);
            if (parsed != null)
            {
                NestEventReceived?.Invoke(parsed);
            }
        }
        else if (string.Equals(eventType, "state_changed", StringComparison.OrdinalIgnoreCase))
        {
            StateChangedPayload? parsed = PersonDetectionController.TryParseStateChangedJson(evt);
            if (parsed != null)
            {
                StateChangedReceived?.Invoke(parsed);
            }
        }
    }

    public async Task<IReadOnlyList<HaCameraDiscoveryItem>> GetDiscoveredCamerasAsync(CancellationToken ct = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open || ConnectionState != HaConnectionState.Connected)
        {
            throw new InvalidOperationException("Not connected to Home Assistant WebSocket.");
        }

        JsonElement statesResponse = await SendCommandAsync(new { type = "get_states" }, ct);

        var entityToDeviceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deviceToPersonEventEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            JsonElement regResponse = await SendCommandAsync(new { type = "config/entity_registry/list" }, ct);
            if (regResponse.TryGetProperty("result", out JsonElement regArr) &&
                regArr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in regArr.EnumerateArray())
                {
                    string? eid = item.TryGetProperty("entity_id", out JsonElement eidProp) ? eidProp.GetString() : null;
                    string? did = item.TryGetProperty("device_id", out JsonElement didProp) ? didProp.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(eid) && !string.IsNullOrWhiteSpace(did))
                    {
                        entityToDeviceId[eid] = did;
                        if (eid.StartsWith("event.", StringComparison.OrdinalIgnoreCase) &&
                            (eid.EndsWith("_motion", StringComparison.OrdinalIgnoreCase) ||
                             eid.EndsWith("_person", StringComparison.OrdinalIgnoreCase)))
                        {
                            deviceToPersonEventEntity[did] = eid;
                        }
                    }
                }
            }
        }
        catch
        {
            // Continue gracefully if user account doesn't have registry list permission
        }

        var cameras = new List<(string EntityId, string FriendlyName)>();
        var personEventStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (statesResponse.TryGetProperty("result", out JsonElement resultArr) &&
            resultArr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in resultArr.EnumerateArray())
            {
                string? entityId = item.TryGetProperty("entity_id", out JsonElement eidProp)
                    ? eidProp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(entityId)) continue;

                if (entityId.StartsWith("camera.", StringComparison.OrdinalIgnoreCase))
                {
                    string friendlyName = entityId;
                    if (item.TryGetProperty("attributes", out JsonElement attrs) &&
                        attrs.ValueKind == JsonValueKind.Object &&
                        attrs.TryGetProperty("friendly_name", out JsonElement fnProp))
                    {
                        friendlyName = fnProp.GetString() ?? entityId;
                    }
                    cameras.Add((entityId, friendlyName));
                }
                else if (entityId.StartsWith("event.", StringComparison.OrdinalIgnoreCase))
                {
                    bool supportsCameraPerson =
                        entityId.EndsWith("_motion", StringComparison.OrdinalIgnoreCase) ||
                        entityId.EndsWith("_person", StringComparison.OrdinalIgnoreCase);

                    string? evtType = null;
                    string? nestEvtId = null;

                    if (item.TryGetProperty("attributes", out JsonElement attrs) &&
                        attrs.ValueKind == JsonValueKind.Object)
                    {
                        if (attrs.TryGetProperty("event_types", out JsonElement etArr) &&
                            etArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement et in etArr.EnumerateArray())
                            {
                                if (string.Equals(et.GetString(), "camera_person", StringComparison.OrdinalIgnoreCase))
                                {
                                    supportsCameraPerson = true;
                                }
                            }
                        }

                        if (attrs.TryGetProperty("event_type", out JsonElement etProp))
                            evtType = etProp.GetString();
                        if (attrs.TryGetProperty("nest_event_id", out JsonElement nidProp))
                            nestEvtId = nidProp.GetString();
                    }

                    if (supportsCameraPerson)
                    {
                        string? st = item.TryGetProperty("state", out JsonElement stProp) ? stProp.GetString() : null;
                        personEventStates[entityId] = $"{st}|{evtType}|{nestEvtId}";
                    }
                }
            }
        }

        var list = new List<HaCameraDiscoveryItem>();
        foreach (var (camEntityId, friendlyName) in cameras)
        {
            entityToDeviceId.TryGetValue(camEntityId, out string? deviceId);
            deviceId ??= string.Empty;

            string? personEventEntityId = null;
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                deviceToPersonEventEntity.TryGetValue(deviceId, out personEventEntityId);
            }

            if (string.IsNullOrWhiteSpace(personEventEntityId))
            {
                string slug = camEntityId["camera.".Length..];
                personEventEntityId = personEventStates.Keys.FirstOrDefault(e =>
                    e.Contains(slug, StringComparison.OrdinalIgnoreCase))
                    ?? DeriveDefaultNestMotionEventEntityId(camEntityId);
            }

            personEventStates.TryGetValue(personEventEntityId, out string? initialToken);

            list.Add(new HaCameraDiscoveryItem(
                camEntityId,
                friendlyName,
                deviceId,
                personEventEntityId,
                initialToken ?? string.Empty));
        }

        return list
            .OrderBy(c => c.FriendlyName)
            .ThenBy(c => c.EntityId)
            .ToList();
    }

    private async Task<JsonElement> SendCommandAsync(object commandWithoutId, CancellationToken ct)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket is not open.");
        }

        int id = Interlocked.Increment(ref _nextMessageId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        using JsonDocument doc = JsonSerializer.SerializeToDocument(commandWithoutId);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        string payload = Encoding.UTF8.GetString(ms.ToArray());
        await SendRawJsonAsync(_webSocket, payload, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

        return await tcs.Task;
    }

    public async Task<string?> GetWebRtcClientConfigJsonAsync(string cameraEntityId, CancellationToken ct = default)
    {
        try
        {
            if (!await EnsureConnectedAsync(TimeSpan.FromSeconds(5), ct))
            {
                return null;
            }

            JsonElement resp = await SendCommandAsync(new
            {
                type = "camera/webrtc/get_client_config",
                entity_id = cameraEntityId
            }, ct);

            if (resp.TryGetProperty("result", out JsonElement res) && res.ValueKind == JsonValueKind.Object)
            {
                return res.GetRawText();
            }
        }
        catch
        {
            // Fall back to default STUN config
        }

        return null;
    }

    public async Task StartWebRtcOfferAsync(string cameraEntityId, string sdpOffer, CancellationToken ct = default)
    {
        if (!await EnsureConnectedAsync(TimeSpan.FromSeconds(5), ct) || _webSocket == null)
        {
            WebRtcErrorReceived?.Invoke("Not connected to Home Assistant WebSocket.");
            return;
        }

        int id = Interlocked.Increment(ref _nextMessageId);
        var resultTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingRequests[id] = resultTcs;
        _eventSubscriptions[id] = evtRoot =>
        {
            if (!evtRoot.TryGetProperty("event", out JsonElement evtObj)) return;
            string? evtType = evtObj.TryGetProperty("type", out JsonElement tProp) ? tProp.GetString() : null;

            if (string.Equals(evtType, "session", StringComparison.OrdinalIgnoreCase))
            {
                string? sessionId = evtObj.TryGetProperty("session_id", out JsonElement sidProp)
                    ? sidProp.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    WebRtcSessionReceived?.Invoke(sessionId);
                }
            }
            else if (string.Equals(evtType, "answer", StringComparison.OrdinalIgnoreCase))
            {
                string? answerSdp = evtObj.TryGetProperty("answer", out JsonElement ansProp)
                    ? ansProp.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(answerSdp))
                {
                    WebRtcAnswerReceived?.Invoke(answerSdp);
                }
            }
            else if (string.Equals(evtType, "candidate", StringComparison.OrdinalIgnoreCase))
            {
                if (evtObj.TryGetProperty("candidate", out JsonElement candProp))
                {
                    WebRtcRemoteCandidateReceived?.Invoke(candProp.GetRawText());
                }
            }
            else if (string.Equals(evtType, "error", StringComparison.OrdinalIgnoreCase))
            {
                string? msg = evtObj.TryGetProperty("message", out JsonElement mProp)
                    ? mProp.GetString()
                    : "WebRTC stream negotiation error";
                WebRtcErrorReceived?.Invoke(msg ?? "WebRTC error");
            }
        };

        string payload = JsonSerializer.Serialize(new
        {
            id,
            type = "camera/webrtc/offer",
            entity_id = cameraEntityId,
            offer = sdpOffer
        });

        await SendRawJsonAsync(_webSocket, payload, ct);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var reg = timeoutCts.Token.Register(() => resultTcs.TrySetCanceled());

            JsonElement res = await resultTcs.Task;

            if (res.TryGetProperty("success", out JsonElement succProp) && succProp.ValueKind == JsonValueKind.False)
            {
                string errMsg = "camera/webrtc/offer failed";
                if (res.TryGetProperty("error", out JsonElement errObj) &&
                    errObj.TryGetProperty("message", out JsonElement msgProp))
                {
                    errMsg = msgProp.GetString() ?? errMsg;
                }
                WebRtcErrorReceived?.Invoke(errMsg);
                return;
            }

            if (res.TryGetProperty("result", out JsonElement resBody) &&
                resBody.ValueKind == JsonValueKind.Object &&
                resBody.TryGetProperty("answer", out JsonElement directAns))
            {
                string? sdp = directAns.GetString();
                if (!string.IsNullOrWhiteSpace(sdp))
                {
                    WebRtcAnswerReceived?.Invoke(sdp);
                }
            }
        }
        catch (Exception ex)
        {
            WebRtcErrorReceived?.Invoke(ex.Message);
        }
    }

    public async Task SendWebRtcCandidateAsync(
        string cameraEntityId,
        string sessionId,
        JsonElement candidateElement,
        CancellationToken ct = default)
    {
        if (_webSocket == null || _webSocket.State != WebSocketState.Open || ConnectionState != HaConnectionState.Connected)
        {
            return;
        }

        try
        {
            int id = Interlocked.Increment(ref _nextMessageId);
            string payload = JsonSerializer.Serialize(new
            {
                id,
                type = "camera/webrtc/candidate",
                entity_id = cameraEntityId,
                session_id = sessionId,
                candidate = candidateElement
            });

            await SendRawJsonAsync(_webSocket, payload, ct);
        }
        catch
        {
            // Ignore non-fatal candidate send errors
        }
    }

    private async Task SendRawJsonAsync(ClientWebSocket ws, string json, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (ws.State == WebSocketState.Open)
            {
                await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string> ReceiveFullTextMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();

        while (true)
        {
            WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("Home Assistant closed the WebSocket connection.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void FailPendingRequests(Exception ex)
    {
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    public void Dispose()
    {
        Stop();
        _httpClient.Dispose();
        _sendLock.Dispose();
    }
}
