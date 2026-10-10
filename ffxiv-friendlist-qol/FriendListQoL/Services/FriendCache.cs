using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FriendListQoL.Models;
using Lumina.Excel.Sheets;

namespace FriendListQoL.Services;

/// <summary>
/// Reads the game's friend list through InfoProxyFriendList, the same data
/// source the native Friend List window uses. Refreshes whenever the game
/// finishes a friend-data request (EndRequest hook).
/// </summary>
public sealed unsafe class FriendCache : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGameInteropProvider _interop;
    private readonly IDataManager _dataManager;
    private readonly Configuration _config;

    private Hook<InfoProxyCommonList.Delegates.EndRequest>? _endRequestHook;
    private bool _refreshPending;
    private DateTime _lastSeenSave = DateTime.MinValue;

    public List<FriendEntry> Friends { get; private set; } = new();
    public event Action? Changed;

    /// <summary>
    /// When true, the cache serves generated fake friends instead of reading
    /// the game. Used to test the UI and purge flow safely.
    /// </summary>
    public bool UseTestData { get; private set; }

    public FriendCache(Configuration config, IPluginLog log, IFramework framework, IGameInteropProvider interop, IDataManager dataManager)
    {
        _config = config;
        _log = log;
        _framework = framework;
        _interop = interop;
        _dataManager = dataManager;

        var proxy = InfoProxyFriendList.Instance();
        if (proxy != null)
        {
            _endRequestHook = _interop.HookFromAddress<InfoProxyCommonList.Delegates.EndRequest>(
                ((InfoProxyCommonList*)proxy)->VirtualTable->EndRequest,
                EndRequestDetour);
            _endRequestHook.Enable();
        }
        else
        {
            _log.Warning("InfoProxyFriendList instance was null; friend-data hook not installed.");
        }

        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _endRequestHook?.Dispose();
        try { _config.Save(); } catch { /* best effort: persist last-seen timestamps */ }
    }

    /// <summary>
    /// Ask the server for fresh friend data. The EndRequest hook fires when it
    /// arrives and the cache rebuilds on the next framework tick.
    /// </summary>
    public void RequestRefresh()
    {
        var proxy = InfoProxyFriendList.Instance();
        if (proxy == null)
            return;
        ((InfoProxyCommonList*)proxy)->RequestData();
    }

    private void EndRequestDetour(InfoProxyCommonList* proxy)
    {
        _endRequestHook!.Original(proxy);
        if (proxy == (InfoProxyCommonList*)InfoProxyFriendList.Instance())
            _refreshPending = true;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_refreshPending)
            return;
        _refreshPending = false;
        if (UseTestData)
            return; // real game data must never overwrite the fake test list
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to refresh friend cache");
        }
    }

    private void Refresh()
    {
        var next = new List<FriendEntry>();
        var proxy = InfoProxyFriendList.Instance();
        if (proxy != null)
        {
            // Cast to the base type the way the game organizes it: the entry
            // list lives on InfoProxyCommonList.
            var common = (InfoProxyCommonList*)proxy;
            var span = common->CharDataSpan;
            for (var i = 0; i < span.Length; i++)
            {
                ref readonly var data = ref span[i];
                if (data.ContentId == 0)
                    continue;

                var name = data.NameString;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                next.Add(new FriendEntry
                {
                    ContentId = data.ContentId,
                    Name = name,
                    HomeWorldId = data.HomeWorld,
                    HomeWorldName = ResolveWorldName(data.HomeWorld),
                    JobId = data.Job,
                    JobName = ResolveJobName(data.Job),
                    IsOnline = data.State != 0 &&
                               !data.State.HasFlag(InfoProxyCommonList.CharacterData.OnlineStatus.NotFound),
                    LastSeenUnix = _config.LastSeenOnline.TryGetValue(data.ContentId, out var seen) ? seen : 0,
                });
            }
        }

        StampLastSeen(next);
        Friends = next;
        Changed?.Invoke();
    }

    /// <summary>
    /// Records "now" for every friend currently online. The game doesn't
    /// expose last-online, so the plugin builds this history itself.
    /// Saves at most once a minute to avoid disk churn.
    /// </summary>
    private void StampLastSeen(List<FriendEntry> entries)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var changed = false;
        foreach (var e in entries)
        {
            if (!e.IsOnline)
                continue;
            if (_config.LastSeenOnline.TryGetValue(e.ContentId, out var prev) && now - prev < 60)
                continue;
            _config.LastSeenOnline[e.ContentId] = now;
            changed = true;
        }
        if (changed && DateTime.UtcNow - _lastSeenSave > TimeSpan.FromMinutes(1))
        {
            _config.Save();
            _lastSeenSave = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Switches between real game data and generated fake friends.
    /// Turning test mode off re-requests the real list from the server.
    /// </summary>
    public void SetTestMode(bool enabled)
    {
        UseTestData = enabled;
        if (enabled)
            GenerateTestFriends();
        else
            RequestRefresh();
    }

    /// <summary>Builds a deterministic set of 60 fake friends for testing.</summary>
    public void GenerateTestFriends()
    {
        var rng = new Random(1337);
        var used = new HashSet<string>();
        var list = new List<FriendEntry>();

        for (var i = 0; i < 60; i++)
        {
            string name;
            do
            {
                name = $"{TestFirstNames[rng.Next(TestFirstNames.Length)]} {TestLastNames[rng.Next(TestLastNames.Length)]}";
            } while (!used.Add(name));

            var world = TestWorlds[rng.Next(TestWorlds.Length)];
            list.Add(new FriendEntry
            {
                ContentId = 9_000_000UL + (ulong)i,
                Name = name,
                HomeWorldId = 0,
                HomeWorldName = world,
                JobId = 0,
                JobName = TestJobs[rng.Next(TestJobs.Length)],
                IsOnline = rng.NextDouble() < 0.6,
            });
        }

        Friends = list;
        Changed?.Invoke();
    }

    /// <summary>Drops a fake friend from the test list (used by simulated purges).</summary>
    public void RemoveTestFriend(ulong contentId)
    {
        if (!UseTestData)
            return;
        Friends.RemoveAll(e => e.ContentId == contentId);
        Changed?.Invoke();
    }

    private static readonly string[] TestFirstNames =
    [
        "Aera", "Bryn", "Cael", "Dain", "Eira", "Fael", "Garr", "Hilda",
        "Ivo", "Jora", "Kael", "Lira", "Mira", "Nyx", "Orin", "Pella",
        "Quinn", "Rurik", "Sera", "Talon", "Ulma", "Vesper", "Wren", "Xan",
        "Ysolde", "Zev",
    ];

    private static readonly string[] TestLastNames =
    [
        "Ironhold", "Stormborn", "Ashford", "Blackwood", "Crowley", "Duskmere",
        "Elmsworth", "Frostfall", "Grimshaw", "Halloway", "Icebrand", "Jorunn",
        "Kingsley", "Lightfoot", "Marsh", "Nightingale", "Oakenshield", "Pyre",
        "Quill", "Ravensworth", "Stonebridge", "Thorn", "Underbough", "Vex",
        "Willow", "Yew", "Zephyr",
    ];

    private static readonly string[] TestWorlds =
    [
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Sargatanas",
        "Balmung", "Coeurl", "Goblin", "Mateus", "Behemoth", "Excalibur",
        "Leviathan", "Ultros", "Cerberus", "Omega", "Ragnarok", "Shiva",
        "Odin", "Zodiark",
    ];

    private static readonly string[] TestJobs =
    [
        "PLD", "WAR", "DRK", "GNB", "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR", "BRD", "MCH",
        "DNC", "BLM", "SMN", "RDM", "PCT", "BLU",
    ];

    private string ResolveWorldName(ushort worldId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<World>();
            var name = sheet?.GetRow(worldId).Name.ExtractText();
            return string.IsNullOrEmpty(name) ? worldId.ToString() : name;
        }
        catch
        {
            return worldId.ToString();
        }
    }

    private string ResolveJobName(byte jobId)
    {
        try
        {
            var sheet = _dataManager.GetExcelSheet<ClassJob>();
            var abbr = sheet?.GetRow(jobId).Abbreviation.ExtractText();
            return string.IsNullOrEmpty(abbr) ? jobId.ToString() : abbr;
        }
        catch
        {
            return jobId.ToString();
        }
    }
}
