using System.Text.Json;
using Derbell.Models;

namespace Derbell.Services;

public sealed record NestEventPayload(
    string DeviceId,
    string Type,
    string? NestEventId,
    string? Timestamp);

public sealed record StateChangedPayload(
    string EntityId,
    string? OldState,
    string? NewState,
    string? EventTypeAttribute,
    string? DeviceIdAttribute,
    string? OldEventTypeAttribute = null,
    string? NestEventIdAttribute = null);

public enum DetectionDecision
{
    Ignore,
    ShowPopup,
    SwitchCameraPopup,
    ExtendPopup,
    HidePopup
}

public sealed class PersonDetectionController
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldownByCameraKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastSeenPersonTokenByEntity = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config;
    private bool _isPopupVisible;
    private MonitoredCameraConfig? _activeCamera;
    private DateTimeOffset? _lastActivityAt;
    private DateTimeOffset? _snoozedUntil;

    public event Action<MonitoredCameraConfig, string>? ShowPopupRequested;
    public event Action<string>? HidePopupRequested;
    public event Action? ActivityExtended;

    public PersonDetectionController(AppConfig config)
    {
        _config = config;
        _config.MigrateLegacyCameraIfNeeded();
    }

    public bool IsPopupVisible
    {
        get { lock (_sync) return _isPopupVisible; }
    }

    public MonitoredCameraConfig? ActiveCamera
    {
        get { lock (_sync) return _activeCamera; }
    }

    public bool IsSnoozed(DateTimeOffset now)
    {
        lock (_sync)
        {
            return _snoozedUntil.HasValue && now < _snoozedUntil.Value;
        }
    }

    public void UpdateConfig(AppConfig config)
    {
        lock (_sync)
        {
            _config = config;
            _config.MigrateLegacyCameraIfNeeded();
        }
    }

    /// <summary>
    /// Automatically populates DeviceId and PersonEventEntityId on configured cameras when discovered from Home Assistant,
    /// and seeds current event entity states so historical events do not trigger on startup.
    /// </summary>
    public void SyncDiscoveredCameraMetadata(IReadOnlyList<HaCameraDiscoveryItem> discovered)
    {
        lock (_sync)
        {
            foreach (HaCameraDiscoveryItem disc in discovered)
            {
                if (!string.IsNullOrWhiteSpace(disc.PersonEventEntityId) &&
                    !string.IsNullOrWhiteSpace(disc.InitialEventStateToken) &&
                    !_lastSeenPersonTokenByEntity.ContainsKey(disc.PersonEventEntityId))
                {
                    _lastSeenPersonTokenByEntity[disc.PersonEventEntityId] = disc.InitialEventStateToken;
                }
            }

            foreach (MonitoredCameraConfig cam in _config.Cameras)
            {
                HaCameraDiscoveryItem? match = discovered.FirstOrDefault(d =>
                    string.Equals(d.EntityId, cam.EntityId, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    if (!string.IsNullOrWhiteSpace(match.DeviceId))
                        cam.DeviceId = match.DeviceId;
                    if (!string.IsNullOrWhiteSpace(match.PersonEventEntityId))
                        cam.PersonEventEntityId = match.PersonEventEntityId;
                    if (string.IsNullOrWhiteSpace(cam.FriendlyName))
                        cam.FriendlyName = match.FriendlyName;
                }
            }
        }
    }

    public void SetSnooze(TimeSpan? duration, DateTimeOffset now)
    {
        bool shouldHide = false;
        lock (_sync)
        {
            if (duration.HasValue)
            {
                _snoozedUntil = duration.Value == TimeSpan.MaxValue
                    ? DateTimeOffset.MaxValue
                    : now.Add(duration.Value);

                if (_isPopupVisible)
                {
                    _isPopupVisible = false;
                    _activeCamera = null;
                    _lastActivityAt = null;
                    shouldHide = true;
                }
            }
            else
            {
                _snoozedUntil = null;
            }
        }

        if (shouldHide)
        {
            HidePopupRequested?.Invoke("Snoozed");
        }
    }

    /// <summary>
    /// Evaluates a raw Home Assistant nest_event payload across all enabled cameras.
    /// Triggers ONLY on "camera_person", and extends an already-open popup on continuation events for the active camera.
    /// </summary>
    public DetectionDecision ProcessNestEvent(NestEventPayload evt, DateTimeOffset now)
    {
        DetectionDecision decision = DetectionDecision.Ignore;
        MonitoredCameraConfig? targetCamera = null;

        lock (_sync)
        {
            if (_snoozedUntil.HasValue && now < _snoozedUntil.Value)
            {
                return DetectionDecision.Ignore;
            }

            targetCamera = ResolveCameraForNestDevice(evt.DeviceId);
            if (targetCamera == null)
            {
                return DetectionDecision.Ignore;
            }

            string camKey = GetCameraKey(targetCamera, evt.DeviceId);
            if (_cooldownByCameraKey.TryGetValue(camKey, out DateTimeOffset cooldownUntil) && now < cooldownUntil)
            {
                return DetectionDecision.Ignore;
            }

            bool isPersonEvent = string.Equals(evt.Type, "camera_person", StringComparison.OrdinalIgnoreCase);
            bool isSameActiveCamera = _isPopupVisible &&
                _activeCamera != null &&
                string.Equals(_activeCamera.EntityId, targetCamera.EntityId, StringComparison.OrdinalIgnoreCase);

            bool isMotionContinuation = isSameActiveCamera &&
                string.Equals(evt.Type, "camera_motion", StringComparison.OrdinalIgnoreCase);

            if (isPersonEvent)
            {
                _lastActivityAt = now;

                if (!_isPopupVisible)
                {
                    _isPopupVisible = true;
                    _activeCamera = targetCamera;
                    decision = DetectionDecision.ShowPopup;
                }
                else if (!isSameActiveCamera)
                {
                    _activeCamera = targetCamera;
                    decision = DetectionDecision.SwitchCameraPopup;
                }
                else
                {
                    decision = DetectionDecision.ExtendPopup;
                }
            }
            else if (isMotionContinuation)
            {
                _lastActivityAt = now;
                decision = DetectionDecision.ExtendPopup;
            }
        }

        if (decision is DetectionDecision.ShowPopup or DetectionDecision.SwitchCameraPopup && targetCamera != null)
        {
            ShowPopupRequested?.Invoke(targetCamera, $"Person detected on {targetCamera.DisplayLabel}");
        }
        else if (decision == DetectionDecision.ExtendPopup)
        {
            ActivityExtended?.Invoke();
        }

        return decision;
    }

    /// <summary>
    /// Evaluates Home Assistant state_changed events (or entity state polls) for Nest event.<camera>_motion / event.<camera>_person entities.
    /// </summary>
    public DetectionDecision ProcessStateChanged(StateChangedPayload stateChange, DateTimeOffset now)
    {
        DetectionDecision decision = DetectionDecision.Ignore;
        MonitoredCameraConfig? targetCamera = null;
        string reason = string.Empty;

        lock (_sync)
        {
            if (_snoozedUntil.HasValue && now < _snoozedUntil.Value)
            {
                return DetectionDecision.Ignore;
            }

            if (!stateChange.EntityId.StartsWith("event.", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(stateChange.NewState) ||
                string.Equals(stateChange.NewState, "unavailable", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stateChange.NewState, "unknown", StringComparison.OrdinalIgnoreCase))
            {
                return DetectionDecision.Ignore;
            }

            targetCamera = ResolveCameraForEventEntity(stateChange.EntityId, stateChange.DeviceIdAttribute);
            if (targetCamera == null)
            {
                return DetectionDecision.Ignore;
            }

            bool isPersonEvent = string.Equals(stateChange.EventTypeAttribute, "camera_person", StringComparison.OrdinalIgnoreCase);
            bool isSameActiveCamera = _isPopupVisible &&
                _activeCamera != null &&
                string.Equals(_activeCamera.EntityId, targetCamera.EntityId, StringComparison.OrdinalIgnoreCase);

            bool isMotionContinuation = isSameActiveCamera &&
                string.Equals(stateChange.EventTypeAttribute, "camera_motion", StringComparison.OrdinalIgnoreCase);

            string token = $"{stateChange.NewState}|{stateChange.EventTypeAttribute}|{stateChange.NestEventIdAttribute}";
            if (_lastSeenPersonTokenByEntity.TryGetValue(stateChange.EntityId, out string? prevToken) &&
                string.Equals(prevToken, token, StringComparison.Ordinal))
            {
                return DetectionDecision.Ignore;
            }

            // If OldState is provided and identical to NewState, only proceed if event_type upgraded to camera_person
            if (!string.IsNullOrWhiteSpace(stateChange.OldState) &&
                string.Equals(stateChange.OldState, stateChange.NewState, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(stateChange.OldEventTypeAttribute, stateChange.EventTypeAttribute, StringComparison.OrdinalIgnoreCase))
            {
                return DetectionDecision.Ignore;
            }

            _lastSeenPersonTokenByEntity[stateChange.EntityId] = token;

            if (!isPersonEvent && !isMotionContinuation)
            {
                return DetectionDecision.Ignore;
            }

            string camKey = GetCameraKey(targetCamera, stateChange.DeviceIdAttribute);
            if (_cooldownByCameraKey.TryGetValue(camKey, out DateTimeOffset cooldownUntil) && now < cooldownUntil)
            {
                return DetectionDecision.Ignore;
            }

            _lastActivityAt = now;
            if (isPersonEvent)
            {
                if (!_isPopupVisible)
                {
                    _isPopupVisible = true;
                    _activeCamera = targetCamera;
                    decision = DetectionDecision.ShowPopup;
                    reason = $"Person detected on {targetCamera.DisplayLabel}";
                }
                else if (!isSameActiveCamera)
                {
                    _activeCamera = targetCamera;
                    decision = DetectionDecision.SwitchCameraPopup;
                    reason = $"Person detected on {targetCamera.DisplayLabel}";
                }
                else
                {
                    decision = DetectionDecision.ExtendPopup;
                }
            }
            else if (isMotionContinuation)
            {
                decision = DetectionDecision.ExtendPopup;
            }
        }

        if (decision is DetectionDecision.ShowPopup or DetectionDecision.SwitchCameraPopup && targetCamera != null)
        {
            ShowPopupRequested?.Invoke(targetCamera, reason);
        }
        else if (decision == DetectionDecision.ExtendPopup)
        {
            ActivityExtended?.Invoke();
        }

        return decision;
    }

    private MonitoredCameraConfig? ResolveCameraForNestDevice(string? deviceId)
    {
        IReadOnlyList<MonitoredCameraConfig> enabled = _config.GetEnabledCameras();
        if (enabled.Count == 0)
        {
            return new MonitoredCameraConfig
            {
                EntityId = _config.CameraEntityId,
                FriendlyName = _config.CameraFriendlyName,
                DeviceId = deviceId ?? string.Empty,
                Enabled = true
            };
        }

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            MonitoredCameraConfig? exact = enabled.FirstOrDefault(c =>
                string.Equals(c.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }
        }

        return enabled.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.DeviceId));
    }

    private MonitoredCameraConfig? ResolveCameraForEventEntity(string eventEntityId, string? deviceIdAttr)
    {
        IReadOnlyList<MonitoredCameraConfig> enabled = _config.GetEnabledCameras();
        if (enabled.Count == 0)
        {
            return new MonitoredCameraConfig
            {
                EntityId = _config.CameraEntityId,
                FriendlyName = _config.CameraFriendlyName,
                PersonEventEntityId = eventEntityId,
                Enabled = true
            };
        }

        // 1. Exact PersonEventEntityId match
        MonitoredCameraConfig? byEventId = enabled.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c.PersonEventEntityId) &&
            string.Equals(c.PersonEventEntityId, eventEntityId, StringComparison.OrdinalIgnoreCase));
        if (byEventId != null) return byEventId;

        // 2. Exact DeviceId match
        if (!string.IsNullOrWhiteSpace(deviceIdAttr))
        {
            MonitoredCameraConfig? byDevice = enabled.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c.DeviceId) &&
                string.Equals(c.DeviceId, deviceIdAttr, StringComparison.OrdinalIgnoreCase));
            if (byDevice != null) return byDevice;
        }

        // 3. Slug match (e.g. camera.front_porch_camera <-> event.front_porch_camera_motion or event.front_porch_person)
        foreach (MonitoredCameraConfig cam in enabled)
        {
            if (cam.EntityId.StartsWith("camera.", StringComparison.OrdinalIgnoreCase))
            {
                string slug = cam.EntityId["camera.".Length..];
                if (!string.IsNullOrWhiteSpace(slug) &&
                    eventEntityId.Contains(slug, StringComparison.OrdinalIgnoreCase))
                {
                    return cam;
                }
            }
        }

        return null;
    }

    private static string GetCameraKey(MonitoredCameraConfig cam, string? fallbackDeviceId)
    {
        if (!string.IsNullOrWhiteSpace(cam.EntityId))
            return cam.EntityId;
        if (!string.IsNullOrWhiteSpace(cam.DeviceId))
            return cam.DeviceId;
        return fallbackDeviceId ?? "default";
    }

    public bool CheckPresenceWatchdogExpired(DateTimeOffset now)
    {
        bool shouldHide = false;
        lock (_sync)
        {
            if (!_isPopupVisible || !_lastActivityAt.HasValue)
            {
                return false;
            }

            // Nest SDM emits person continuation events roughly every 15-20s; ensure at least 20s after stream start
            int timeoutSec = Math.Max(20, _config.PersonAbsenceTimeoutSeconds);
            if ((now - _lastActivityAt.Value).TotalSeconds >= timeoutSec)
            {
                _isPopupVisible = false;
                _activeCamera = null;
                _lastActivityAt = null;
                shouldHide = true;
            }
        }

        if (shouldHide)
        {
            HidePopupRequested?.Invoke("Person no longer detected (timeout)");
        }

        return shouldHide;
    }

    public void NotifyVideoStreamStarted(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_isPopupVisible)
            {
                _lastActivityAt = now;
            }
        }
    }

    public void TriggerManualShow(DateTimeOffset now, MonitoredCameraConfig? specificCamera = null)
    {
        MonitoredCameraConfig target;
        lock (_sync)
        {
            IReadOnlyList<MonitoredCameraConfig> enabled = _config.GetEnabledCameras();
            target = specificCamera
                ?? _activeCamera
                ?? enabled.FirstOrDefault()
                ?? new MonitoredCameraConfig
                {
                    EntityId = _config.CameraEntityId,
                    FriendlyName = string.IsNullOrWhiteSpace(_config.CameraFriendlyName)
                        ? "Nest Outdoor Cam"
                        : _config.CameraFriendlyName,
                    Enabled = true
                };

            _cooldownByCameraKey.Remove(GetCameraKey(target, target.DeviceId));
            _lastActivityAt = now;
            _activeCamera = target;
            _isPopupVisible = true;
        }

        ShowPopupRequested?.Invoke(target, "Manual test / show camera");
    }

    public void DismissManually(DateTimeOffset now)
    {
        bool wasVisible;
        lock (_sync)
        {
            wasVisible = _isPopupVisible;
            if (_activeCamera != null)
            {
                int cooldownSec = Math.Max(0, _config.ManualDismissCooldownSeconds);
                if (cooldownSec > 0)
                {
                    string key = GetCameraKey(_activeCamera, _activeCamera.DeviceId);
                    _cooldownByCameraKey[key] = now.AddSeconds(cooldownSec);
                }
            }

            _isPopupVisible = false;
            _activeCamera = null;
            _lastActivityAt = null;
        }

        if (wasVisible)
        {
            HidePopupRequested?.Invoke("Dismissed by user");
        }
    }

    public static NestEventPayload? TryParseNestEventJson(JsonElement eventElement)
    {
        try
        {
            if (!eventElement.TryGetProperty("data", out JsonElement data))
                return null;

            string deviceId = data.TryGetProperty("device_id", out JsonElement devProp)
                ? devProp.GetString() ?? string.Empty
                : string.Empty;

            string type = data.TryGetProperty("type", out JsonElement typeProp)
                ? typeProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(type))
                return null;

            string? nestEventId = data.TryGetProperty("nest_event_id", out JsonElement idProp)
                ? idProp.GetString()
                : null;

            string? timestamp = data.TryGetProperty("timestamp", out JsonElement tsProp)
                ? tsProp.GetString()
                : null;

            return new NestEventPayload(deviceId, type, nestEventId, timestamp);
        }
        catch
        {
            return null;
        }
    }

    public static StateChangedPayload? TryParseStateChangedJson(JsonElement eventElement)
    {
        try
        {
            if (!eventElement.TryGetProperty("data", out JsonElement data))
                return null;

            string entityId = data.TryGetProperty("entity_id", out JsonElement entityProp)
                ? entityProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(entityId))
                return null;

            string? oldState = null;
            string? oldEventTypeAttr = null;
            if (data.TryGetProperty("old_state", out JsonElement oldStateEl) &&
                oldStateEl.ValueKind == JsonValueKind.Object)
            {
                if (oldStateEl.TryGetProperty("state", out JsonElement oldVal))
                {
                    oldState = oldVal.GetString();
                }

                if (oldStateEl.TryGetProperty("attributes", out JsonElement oldAttrs) &&
                    oldAttrs.ValueKind == JsonValueKind.Object &&
                    oldAttrs.TryGetProperty("event_type", out JsonElement oldEvtTypeEl))
                {
                    oldEventTypeAttr = oldEvtTypeEl.GetString();
                }
            }

            string? newState = null;
            string? eventTypeAttr = null;
            string? deviceIdAttr = null;
            string? nestEventIdAttr = null;

            if (data.TryGetProperty("new_state", out JsonElement newStateEl) &&
                newStateEl.ValueKind == JsonValueKind.Object)
            {
                if (newStateEl.TryGetProperty("state", out JsonElement newVal))
                {
                    newState = newVal.GetString();
                }

                if (newStateEl.TryGetProperty("attributes", out JsonElement attrs) &&
                    attrs.ValueKind == JsonValueKind.Object)
                {
                    if (attrs.TryGetProperty("event_type", out JsonElement evtTypeEl))
                    {
                        eventTypeAttr = evtTypeEl.GetString();
                    }
                    if (attrs.TryGetProperty("device_id", out JsonElement devIdEl))
                    {
                        deviceIdAttr = devIdEl.GetString();
                    }
                    if (attrs.TryGetProperty("nest_event_id", out JsonElement nestIdEl))
                    {
                        nestEventIdAttr = nestIdEl.GetString();
                    }
                }
            }

            return new StateChangedPayload(
                entityId,
                oldState,
                newState,
                eventTypeAttr,
                deviceIdAttr,
                oldEventTypeAttr,
                nestEventIdAttr);
        }
        catch
        {
            return null;
        }
    }
}
