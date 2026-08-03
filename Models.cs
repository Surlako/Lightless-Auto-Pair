using System;

namespace LightlessAutoPair;

[Serializable]
public sealed class BlacklistEntry
{
    public string HashedCid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public DateTime DeclinedAtUtc { get; set; } = DateTime.UtcNow;

    public string DisplayName
        => string.IsNullOrWhiteSpace(World) ? Name : $"{Name} @ {World}";
}

internal sealed class NearbyPlayer
{
    public object Raw { get; init; } = null!;
    public string HashedCid { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string World { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsPaired { get; init; }
    public bool HasLightlessPendingRequest { get; init; }

    public string FriendlyName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
                return DisplayName;
            if (!string.IsNullOrWhiteSpace(World))
                return $"{Name} @ {World}";
            return string.IsNullOrWhiteSpace(Name) ? HashedCid : Name;
        }
    }
}

[Serializable]
public sealed class PendingRequest
{
    public string HashedCid { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;

    public string FriendlyName
        => !string.IsNullOrWhiteSpace(DisplayName)
            ? DisplayName
            : string.IsNullOrWhiteSpace(World) ? Name : $"{Name} @ {World}";

    internal NearbyPlayer ToNearbyPlayer() => new()
    {
        HashedCid = HashedCid,
        Name = Name,
        World = World,
        DisplayName = DisplayName,
    };
}

internal enum LogKind
{
    Info,
    Contacted,
    Accepted,
    Declined,
    Paused,
    Error,
}

internal sealed class StatusLogEntry
{
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public LogKind Kind { get; init; }
    public string Person { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

internal sealed record LightlessNotificationEvent(
    string Title,
    string Message,
    string ProfileText);

internal enum AutoPairState
{
    Disabled,
    WaitingForLightless,
    Disconnected,
    Ready,
    Sending,
    Error,
}
