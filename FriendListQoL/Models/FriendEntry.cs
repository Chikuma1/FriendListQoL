namespace FriendListQoL.Models;

/// <summary>
/// One row of the friend list, as seen by the plugin.
/// </summary>
public sealed class FriendEntry
{
    public ulong ContentId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ushort HomeWorldId { get; init; }
    public string HomeWorldName { get; init; } = string.Empty;
    public byte JobId { get; init; }
    public string JobName { get; init; } = string.Empty;
    public bool IsOnline { get; init; }

    /// <summary>Unix seconds of the last time the plugin saw this friend online (0 = never).</summary>
    public long LastSeenUnix { get; init; }

    /// <summary>Checked in the purge list. Favorites can never be selected.</summary>
    public bool SelectedForRemoval { get; set; }
}
