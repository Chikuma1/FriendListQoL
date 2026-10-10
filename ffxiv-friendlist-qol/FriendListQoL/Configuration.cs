using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace FriendListQoL;

/// <summary>
/// Persisted settings. Favorites are keyed by friend ContentId, which is
/// stable across name changes, world transfers and data center visits.
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public HashSet<ulong> FavoriteContentIds { get; set; } = new();

    /// <summary>Delay between each automated removal, in milliseconds.</summary>
    public int RemovalDelayMs { get; set; } = 1500;

    /// <summary>
    /// Unix seconds of the last time the plugin saw each friend online.
    /// The game doesn't expose last-online, so this is tracked locally and
    /// only goes back as far as the plugin has been running.
    /// </summary>
    public Dictionary<ulong, long> LastSeenOnline { get; set; } = new();

    /// <summary>User-defined collapsible friend categories.</summary>
    public List<FriendCategory> Categories { get; set; } = new();

    public bool IsConfigWindowOpen { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    public bool IsFavorite(ulong contentId) => FavoriteContentIds.Contains(contentId);

    public void SetFavorite(ulong contentId, bool favorite)
    {
        if (favorite)
            FavoriteContentIds.Add(contentId);
        else
            FavoriteContentIds.Remove(contentId);
        Save();
    }
}

/// <summary>A user-defined, collapsible friend category.</summary>
[Serializable]
public class FriendCategory
{
    public string Name { get; set; } = "New category";
    public HashSet<ulong> MemberContentIds { get; set; } = new();
    public bool Collapsed { get; set; }
}
