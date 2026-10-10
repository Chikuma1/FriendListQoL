using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FriendListQoL.Models;

namespace FriendListQoL.Removal;

/// <summary>
/// Removes friends one at a time through the game's own remove-friend flow,
/// with a configurable delay between each removal. Favorites are never queued.
/// <para/>
/// The queue, pacing, confirmation handling and safety logic are complete.
/// The single step that drives the game's UI (<see cref="TriggerGameRemove"/>)
/// needs in-game calibration — see the comment there and README.
/// </summary>
public sealed unsafe class FriendRemover : IDisposable
{
    private enum Step
    {
        Idle,
        Triggering,
        WaitingForConfirm,
        CoolingDown,
        Finished,
        Failed,
    }

    private const string FriendListAddonName = "FriendList";
    private const string ConfirmAddonName = "SelectYesno";

    /// <summary>How long to wait for the game's confirm dialog after triggering a removal.</summary>
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGameGui _gameGui;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly Configuration _config;

    private readonly Queue<FriendEntry> _queue = new();
    private readonly List<string> _removedNames = new();

    private Step _step = Step.Idle;
    private FriendEntry? _current;
    private DateTime _nextActionAt = DateTime.MinValue;
    private DateTime _confirmDeadline = DateTime.MinValue;
    private DateTime _simConfirmAt = DateTime.MinValue;
    private bool _confirmClicked;

    public bool IsRunning => _step is Step.Triggering or Step.WaitingForConfirm or Step.CoolingDown;
    public bool HasFinished => _step == Step.Finished;
    public bool HasFailed => _step == Step.Failed;

    public int TotalCount { get; private set; }
    public int RemovedCount => _removedNames.Count;
    public int RemainingCount => _queue.Count + (_current != null && IsRunning ? 1 : 0);
    public string? CurrentName => _current?.Name;
    public string? Error { get; private set; }
    public IReadOnlyList<string> RemovedNames => _removedNames;

    /// <summary>
    /// When true, removals are simulated: no game interaction happens, and the
    /// confirm dialog is faked. Pair with <see cref="OnSimulatedRemove"/> to
    /// update the test friend list.
    /// </summary>
    public bool SimulateRemovals { get; set; }

    /// <summary>Called with the friend's ContentId after each simulated removal.</summary>
    public Action<ulong>? OnSimulatedRemove { get; set; }

    public event Action? StateChanged;

    public FriendRemover(IPluginLog log, IFramework framework, IGameGui gameGui, IAddonLifecycle addonLifecycle, Configuration config)
    {
        _log = log;
        _framework = framework;
        _gameGui = gameGui;
        _addonLifecycle = addonLifecycle;
        _config = config;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, ConfirmAddonName, OnConfirmDialogOpened);
        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, ConfirmAddonName, OnConfirmDialogOpened);
    }

    /// <summary>
    /// Starts removing the given friends. Favorites are filtered out
    /// defensively even if the caller forgot to.
    /// </summary>
    public void Start(IEnumerable<FriendEntry> targets)
    {
        if (IsRunning)
            return;

        _queue.Clear();
        _removedNames.Clear();
        Error = null;
        _current = null;

        foreach (var entry in targets.Where(e => !_config.IsFavorite(e.ContentId)))
            _queue.Enqueue(entry);

        TotalCount = _queue.Count;
        if (TotalCount == 0)
        {
            Error = "Nothing to remove.";
            _step = Step.Failed;
            StateChanged?.Invoke();
            return;
        }

        _step = Step.Triggering;
        _log.Info($"Friend purge started: {TotalCount} friends queued.");
        StateChanged?.Invoke();
    }

    /// <summary>Stops after the current friend. Already-removed friends stay removed.</summary>
    public void Cancel()
    {
        if (!IsRunning)
            return;
        _queue.Clear();
        _current = null;
        _step = Step.Finished;
        _log.Info($"Friend purge cancelled by user. Removed {_removedNames.Count} of {TotalCount}.");
        StateChanged?.Invoke();
    }

    /// <summary>Clears the finished/failed state so a new run can start.</summary>
    public void Reset()
    {
        if (IsRunning)
            return;
        _step = Step.Idle;
        Error = null;
        TotalCount = 0;
        _removedNames.Clear();
        StateChanged?.Invoke();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!IsRunning)
            return;

        var now = DateTime.UtcNow;

        switch (_step)
        {
            case Step.Triggering:
                _current = _queue.Dequeue();
                _confirmClicked = false;
                if (TriggerGameRemove(_current))
                {
                    _step = Step.WaitingForConfirm;
                    _confirmDeadline = now + ConfirmTimeout;
                    _simConfirmAt = now + TimeSpan.FromMilliseconds(600); // fake dialog round-trip
                }
                else
                {
                    Fail(Error ?? "Failed to trigger removal.");
                }
                StateChanged?.Invoke();
                break;

            case Step.WaitingForConfirm:
                if (SimulateRemovals)
                {
                    if (now >= _simConfirmAt)
                        CompleteCurrentRemoval();
                }
                else if (now > _confirmDeadline)
                {
                    Fail($"The game's confirmation dialog never appeared for {_current?.Name}. " +
                         "The remove trigger needs calibration — see README.");
                }
                StateChanged?.Invoke();
                break;

            case Step.CoolingDown:
                if (now >= _nextActionAt)
                {
                    _current = null;
                    _step = _queue.Count > 0 ? Step.Triggering : Step.Finished;
                    if (_step == Step.Finished)
                        _log.Info($"Friend purge finished. Removed {_removedNames.Count} of {TotalCount}.");
                }
                StateChanged?.Invoke();
                break;
        }
    }

    private void Fail(string message)
    {
        Error = message;
        _step = Step.Failed;
        _current = null;
        _log.Error($"Friend purge failed: {message} ({_removedNames.Count} of {TotalCount} removed before the failure.)");
    }

    /// <summary>
    /// Fired when the game's Yes/No dialog opens. If we're waiting on a
    /// removal confirmation, click "Yes" to complete this friend's removal.
    /// </summary>
    private unsafe void OnConfirmDialogOpened(AddonEvent type, AddonArgs args)
    {
        if (_step != Step.WaitingForConfirm || _confirmClicked)
            return;

        var addon = (AddonSelectYesno*)(nint)args.Addon;
        if (addon == null || !args.Addon.IsVisible)
            return;

        if (ClickYes(addon))
        {
            _confirmClicked = true;
            CompleteCurrentRemoval();
        }
    }

    /// <summary>
    /// Records one finished removal and moves to the cooldown before the next.
    /// In test mode this also notifies the fake friend list so the entry
    /// disappears from the UI.
    /// </summary>
    private void CompleteCurrentRemoval()
    {
        _removedNames.Add(_current?.Name ?? "???");
        if (SimulateRemovals && _current != null)
            OnSimulatedRemove?.Invoke(_current.ContentId);
        _step = Step.CoolingDown;
        _nextActionAt = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(500, _config.RemovalDelayMs));
        _log.Info($"Removed {_current?.Name} ({_removedNames.Count}/{TotalCount}).");
    }

    /// <summary>
    /// Clicks "Yes" on a SelectYesno dialog.
    /// ⚠ VERIFY IN-GAME: the callback value below is the commonly observed one
    /// for "yes", but confirm it with a FireCallback trace before trusting a
    /// bulk run. If it misbehaves, use ClickLib's ClickSelectYesno instead.
    /// </summary>
    private static bool ClickYes(AddonSelectYesno* addon)
    {
        try
        {
            var values = stackalloc AtkValue[1];
            values[0].Type = AtkValueType.Int;
            values[0].Int = 0; // 0 = Yes, 1 = No (verify in-game)
            addon->AtkUnitBase.FireCallback(1, values, false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Drives the game's own remove-friend flow for one friend.
    /// Returns true once the game's confirmation dialog should be on its way.
    /// </summary>
    private bool TriggerGameRemove(FriendEntry entry)
    {
        // Test mode: simulate the whole flow, touching nothing in the game.
        if (SimulateRemovals)
        {
            _log.Info($"[TEST] Simulating removal of {entry.Name}.");
            return true;
        }

        // The game's Friend List window must be open: the remove flow acts on
        // its rows.
        if (_gameGui.GetAddonByName(FriendListAddonName) == nint.Zero)
        {
            Error = "Open the game's Friend List window first (Social → Friend List), then start the purge.";
            return false;
        }

        // ═══════════════════════════════════════════════════════════════════
        // ⚠ IN-GAME CALIBRATION REQUIRED — see README "Calibrating removal".
        //
        // Manually, removing a friend is: right-click their row in the game's
        // Friend List → context menu → the remove entry → confirm dialog.
        //
        // To finish this method:
        //   1. In a scratch plugin, hook AtkUnitBase.FireCallback and log
        //      (addon name, callback id, values).
        //   2. Manually remove one friend via the game's Friend List.
        //   3. Note the callback id + values the game fires for row selection
        //      and for the context-menu "remove" choice.
        //   4. Replay those calls here for the target's row, then delete this
        //      early-return.
        //
        // Until then this refuses to run so the queue can never fire blind.
        // ═══════════════════════════════════════════════════════════════════
        _log.Warning($"Remove trigger not calibrated; refusing to remove {entry.Name}.");
        Error = "The removal driver isn't calibrated yet — see README 'Calibrating removal'.";
        return false;
    }
}
