using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LightlessAutoPair;

internal sealed class AutoPairController : IDisposable
{
    private readonly Configuration configuration;
    private readonly LightlessBridge bridge;
    private readonly ConcurrentQueue<LightlessNotificationEvent> notifications = new();
    private readonly object syncRoot = new();
    private readonly Dictionary<string, PendingRequest> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StatusLogEntry> log = new();

    private Task? updateTask;
    private DateTime nextUpdateUtc = DateTime.MinValue;
    private DateTime lastRequestUtc = DateTime.MinValue;
    private AutoPairState lastReportedState = (AutoPairState)(-1);
    private bool disposed;

    public AutoPairController(Configuration configuration)
    {
        this.configuration = configuration;
        bridge = new LightlessBridge(notifications.Enqueue);
        foreach (var request in configuration.OutgoingPendingRequests
                     .Where(request => !string.IsNullOrWhiteSpace(request.HashedCid))
                     .GroupBy(request => request.HashedCid, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(request => request.SentAtUtc).First()))
        {
            pending[request.HashedCid] = request;
        }

        configuration.OutgoingPendingRequests = pending.Values.ToList();
        configuration.Save();
        AddLog(LogKind.Info, string.Empty, "Lightless Auto Pair loaded. Master toggle is off by default.");
    }

    public AutoPairState State { get; private set; } = AutoPairState.Disabled;
    public int NearbyCount { get; private set; }
    public int EligibleCount { get; private set; }
    public string CompatibilityStatus => bridge.CompatibilityStatus;
    public bool LightlessDetected => bridge.IsDetected;
    public bool LightlessLoaded => bridge.IsLoaded;
    public bool LightlessConnected => bridge.IsConnected;

    public int PendingCount
    {
        get
        {
            lock (syncRoot)
                return pending.Count;
        }
    }

    public void OnFrameworkUpdate()
    {
        if (disposed || DateTime.UtcNow < nextUpdateUtc)
            return;
        if (updateTask is { IsCompleted: false })
            return;

        nextUpdateUtc = DateTime.UtcNow.AddMilliseconds(500);
        updateTask = UpdateAsync();
    }

    public IReadOnlyList<StatusLogEntry> GetLogSnapshot()
    {
        lock (syncRoot)
            return log.ToArray();
    }

    public IReadOnlyList<PendingRequest> GetPendingSnapshot()
    {
        lock (syncRoot)
            return pending.Values.OrderBy(entry => entry.SentAtUtc).ToArray();
    }

    public void ClearLog()
    {
        lock (syncRoot)
            log.Clear();
    }

    public void ClearPendingTracker()
    {
        lock (syncRoot)
        {
            pending.Clear();
            configuration.OutgoingPendingRequests.Clear();
            configuration.Save();
        }
        AddLog(LogKind.Info, string.Empty, "Persistent outgoing-pending tracker cleared manually.");
    }

    private async Task UpdateAsync()
    {
        try
        {
            bridge.Refresh();
            ProcessNotifications();

            var nearby = bridge.GetNearbyPlayers();
            NearbyCount = nearby.Count;
            ResolveAcceptedRequests(nearby);

            var eligible = nearby
                .Where(player => !player.IsPaired)
                .Where(player => !player.HasLightlessPendingRequest)
                .Where(player => !IsLocallyPending(player.HashedCid))
                .Where(player => !configuration.IsBlacklisted(player.HashedCid))
                .OrderBy(player => player.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            EligibleCount = eligible.Length;

            if (!configuration.Enabled)
            {
                SetState(AutoPairState.Disabled, "Automatic pairing is disabled.");
                return;
            }

            if (!bridge.IsLoaded)
            {
                SetState(AutoPairState.WaitingForLightless, "Paused: Lightless Sync is not loaded.");
                return;
            }

            if (!bridge.IsConnected)
            {
                SetState(AutoPairState.Disconnected, "Paused automatically: Lightless is disconnected.");
                return;
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(configuration.DelaySeconds, 3, 30));
            if (DateTime.UtcNow - lastRequestUtc < delay || eligible.Length == 0)
            {
                SetState(AutoPairState.Ready, eligible.Length == 0
                    ? "Connected. No eligible nearby Lightfinder users."
                    : "Connected. Waiting for the configured delay.");
                return;
            }

            var candidate = eligible[0];
            SetState(AutoPairState.Sending, $"Sending a pairing request to {candidate.FriendlyName}.");
            AddPending(candidate);
            lastRequestUtc = DateTime.UtcNow;

            try
            {
                await bridge.SendPairRequestAsync(candidate).ConfigureAwait(false);
                AddLog(LogKind.Contacted, candidate.FriendlyName, "Pairing request sent.");
                SetState(AutoPairState.Ready, "Pairing request sent; waiting for the next delay.");
            }
            catch (Exception ex)
            {
                RemovePending(candidate.HashedCid);
                AddLog(LogKind.Error, candidate.FriendlyName,
                    $"Request failed: {ex.GetBaseException().Message}");
                SetState(AutoPairState.Error, "The most recent pairing request failed.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Lightless Auto Pair update failed.");
            AddLog(LogKind.Error, string.Empty, ex.GetBaseException().Message);
            SetState(AutoPairState.Error, "Update failed; see the status log.");
        }
    }

    private void ProcessNotifications()
    {
        while (notifications.TryDequeue(out var notification))
        {
            var combined = string.Join(" | ", notification.Title, notification.Message, notification.ProfileText);
            if (!combined.Contains("declin", StringComparison.OrdinalIgnoreCase) ||
                !combined.Contains("pair", StringComparison.OrdinalIgnoreCase))
                continue;

            var match = MatchPendingRequest(combined);
            if (match is null)
            {
                AddLog(LogKind.Error, string.Empty,
                    $"Lightless reported a declined pair request, but the person could not be identified: {notification.Title} {notification.Message}".Trim());
                continue;
            }

            RemovePending(match.HashedCid);
            configuration.AddToBlacklist(match.ToNearbyPlayer());
            AddLog(LogKind.Declined, match.FriendlyName,
                "Request declined. Added permanently to the decline blacklist.");
        }
    }

    private PendingRequest? MatchPendingRequest(string notificationText)
    {
        PendingRequest[] snapshot;
        lock (syncRoot)
            snapshot = pending.Values.ToArray();

        var matches = snapshot.Where(entry =>
                ContainsIdentifier(notificationText, entry.HashedCid) ||
                ContainsIdentifier(notificationText, entry.DisplayName) ||
                ContainsIdentifier(notificationText, entry.Name) ||
                ContainsIdentifier(notificationText, entry.World))
            .ToArray();

        if (matches.Length == 1)
            return matches[0];

        // Lightless 3.2.3 sometimes emits a generic decline notification. When exactly one
        // outgoing request is pending, that request is the only safe unambiguous match.
        return snapshot.Length == 1 ? snapshot[0] : null;
    }

    private static bool ContainsIdentifier(string text, string identifier)
        => !string.IsNullOrWhiteSpace(identifier) &&
           identifier.Length >= 3 &&
           text.Contains(identifier, StringComparison.OrdinalIgnoreCase);

    private void ResolveAcceptedRequests(IReadOnlyList<NearbyPlayer> nearby)
    {
        foreach (var player in nearby.Where(player => player.IsPaired))
        {
            PendingRequest? resolved;
            lock (syncRoot)
            {
                if (!pending.Remove(player.HashedCid, out resolved))
                    continue;
                SavePendingUnsafe();
            }

            AddLog(LogKind.Accepted, resolved?.FriendlyName ?? player.FriendlyName,
                "Pairing is active. Removed from pending requests.");
        }
    }

    private bool IsLocallyPending(string hashedCid)
    {
        lock (syncRoot)
            return pending.ContainsKey(hashedCid);
    }

    private void AddPending(NearbyPlayer player)
    {
        lock (syncRoot)
        {
            pending[player.HashedCid] = new PendingRequest
            {
                HashedCid = player.HashedCid,
                Name = player.Name,
                World = player.World,
                DisplayName = player.DisplayName,
                SentAtUtc = DateTime.UtcNow,
            };
            SavePendingUnsafe();
        }
    }

    private void RemovePending(string hashedCid)
    {
        lock (syncRoot)
        {
            if (pending.Remove(hashedCid))
                SavePendingUnsafe();
        }
    }

    private void SavePendingUnsafe()
    {
        configuration.OutgoingPendingRequests = pending.Values
            .OrderBy(request => request.SentAtUtc)
            .ToList();
        configuration.Save();
    }

    private void SetState(AutoPairState state, string message)
    {
        State = state;
        if (lastReportedState == state)
            return;

        lastReportedState = state;
        var kind = state switch
        {
            AutoPairState.Disconnected or AutoPairState.WaitingForLightless => LogKind.Paused,
            AutoPairState.Error => LogKind.Error,
            _ => LogKind.Info,
        };
        AddLog(kind, string.Empty, message);
    }

    private void AddLog(LogKind kind, string person, string message)
    {
        lock (syncRoot)
        {
            log.Add(new StatusLogEntry
            {
                TimestampUtc = DateTime.UtcNow,
                Kind = kind,
                Person = person,
                Message = message,
            });

            if (log.Count > 300)
                log.RemoveRange(0, log.Count - 300);
        }
    }

    public void Dispose()
    {
        disposed = true;
        bridge.Dispose();
    }
}
