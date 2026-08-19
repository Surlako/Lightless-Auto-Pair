using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace LightlessAutoPair;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = false;
    public int DelaySeconds { get; set; } = 5;
    public List<BlacklistEntry> DeclinedBlacklist { get; set; } = new();
    public List<PendingRequest> OutgoingPendingRequests { get; set; } = new();

    public bool Normalize()
    {
        var changed = false;
        var normalizedDelay = Math.Clamp(DelaySeconds, 3, 30);
        if (DelaySeconds != normalizedDelay)
        {
            DelaySeconds = normalizedDelay;
            changed = true;
        }

        if (DeclinedBlacklist is null)
        {
            DeclinedBlacklist = new List<BlacklistEntry>();
            changed = true;
        }

        if (OutgoingPendingRequests is null)
        {
            OutgoingPendingRequests = new List<PendingRequest>();
            changed = true;
        }

        return changed;
    }

    public bool IsBlacklisted(string hashedCid)
        => !string.IsNullOrWhiteSpace(hashedCid) &&
           DeclinedBlacklist.Exists(entry =>
               entry.HashedCid.Equals(hashedCid, StringComparison.OrdinalIgnoreCase));

    internal void AddToBlacklist(NearbyPlayer player)
    {
        if (string.IsNullOrWhiteSpace(player.HashedCid) || IsBlacklisted(player.HashedCid))
            return;

        DeclinedBlacklist.Add(new BlacklistEntry
        {
            HashedCid = player.HashedCid,
            Name = player.Name,
            World = player.World,
            DeclinedAtUtc = DateTime.UtcNow,
        });

        Save();
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
