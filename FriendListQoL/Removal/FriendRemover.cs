using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
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

    /// <summary>
    /// True when we're waiting for the user to manually trigger the remove
    /// (right-click → Remove from Friend List) in guided mode.
    /// </summary>
    public bool IsWaitingForUser => _step == Step.WaitingForConfirm && !SimulateRemovals && !_confirmClicked;

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
                else if (!IsWaitingForUser && now > _confirmDeadline)
                {
                    // Guided mode: no timeout, user triggers at their own pace.
                    // This branch is for fully-automated mode (not yet implemented).
                    Fail($"The game's confirmation dialog never appeared for {_current?.Name}.");
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
    /// removal confirmation, verify the dialog is for the expected friend
    /// and click "Yes" to complete this friend's removal.
    /// <para/>
    /// SAFETY: The prompt must contain the current target's name. This prevents
    /// accidentally confirming a dialog for a different friend (e.g., if the
    /// user manually triggered a remove on someone else).
    /// </summary>
    private unsafe void OnConfirmDialogOpened(AddonEvent type, AddonArgs args)
    {
        if (_step != Step.WaitingForConfirm || _confirmClicked)
            return;

        var addon = (AddonSelectYesno*)(nint)args.Addon;
        if (addon == null || !args.Addon.IsVisible)
            return;

        // Safety: verify the dialog is for the expected friend
        if (_current != null && !SimulateRemovals)
        {
            var prompt = addon->PromptText != null
                ? addon->PromptText->NodeText.ToString()
                : string.Empty;
            
            // The prompt should contain the friend's name
            // Use the first name part (before space) for matching, as the prompt
            // may have formatting codes
            var firstName = _current.Name.Split(' ')[0];
            if (!prompt.Contains(firstName, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warning($"SelectYesno dialog prompt doesn't match expected friend '{_current.Name}'. Prompt: '{prompt}'. NOT auto-confirming.");
                return;
            }
        }

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
    /// Clicks "Yes" on a SelectYesno dialog and closes it.
    /// </summary>
    private static bool ClickYes(AddonSelectYesno* addon)
    {
        try
        {
            var values = stackalloc AtkValue[1];
            values[0].Type = AtkValueType.Int;
            values[0].Int = 0; // 0 = Yes, 1 = No
            addon->AtkUnitBase.FireCallback(1, values, true); // true = close dialog after callback
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// When true, attempts full automation by setting the agent selection and
    /// triggering the remove dialog programmatically. When false (default),
    /// uses guided mode where the user manually right-clicks → Remove.
    /// </summary>
    public bool AttemptFullAutomation { get; set; } = false;

    /// <summary>
    /// Drives the game's own remove-friend flow for one friend.
    /// Returns true once the game's confirmation dialog should be on its way.
    /// <para/>
    /// FULL AUTOMATION (experimental): Sets AgentFriendlist.SelectedContentId
    /// to target the friend, then attempts to trigger the remove confirmation
    /// dialog. If this doesn't work, falls back to guided mode.
    /// <para/>
    /// GUIDED MODE (default): The user manually right-clicks → Remove for each
    /// friend, and the plugin auto-clicks Yes on the confirmation dialog.
    /// </summary>
    private unsafe bool TriggerGameRemove(FriendEntry entry)
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

        if (AttemptFullAutomation)
        {
            return TriggerFullAutomation(entry);
        }

        // Guided mode: prompt the user to manually trigger the remove.
        // OnConfirmDialogOpened will auto-click Yes when the dialog appears.
        // No timeout — the user works at their own pace, cancels if needed.
        _confirmDeadline = DateTime.MaxValue;
        _log.Info($"Guided removal: right-click → 'Remove from Friend List' on {entry.Name}. Auto-confirming when the dialog opens.");
        return true;
    }

    /// <summary>
    /// Attempts full automation: sets the agent selection to target the friend,
    /// then triggers the remove confirmation dialog programmatically.
    /// ⚠ EXPERIMENTAL: Test on one friend first with auto-confirm DISABLED!
    /// </summary>
    private unsafe bool TriggerFullAutomation(FriendEntry entry)
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                Error = "AgentModule not available.";
                return false;
            }

            // Set AgentFriendlist selection to target this friend
            var friendAgent = (AgentFriendlist*)agentModule->GetAgentByInternalId(AgentId.Friendlist);
            if (friendAgent == null || friendAgent == (AgentFriendlist*)nint.Zero)
            {
                Error = "AgentFriendlist not available.";
                return false;
            }

            friendAgent->SelectedContentId = entry.ContentId;
            _log.Info($"[FULL-AUTO] Set AgentFriendlist.SelectedContentId={entry.ContentId} for {entry.Name}");

            // For the safe test: we set the selection but DON'T auto-trigger the dialog.
            // The user manually right-clicks → Remove on ANY friend.
            // If the dialog prompt shows OUR target's name (not the right-clicked friend),
            // then the selection override works and we can proceed with full automation.
            // For now, fall through to guided mode behavior (wait for user to trigger).
            
            _log.Warning($"[FULL-AUTO] Selection set for {entry.Name}. Manually right-click → Remove on any friend. If the dialog shows '{entry.Name}', the override works!");
            _confirmDeadline = DateTime.MaxValue;
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Full automation failed: {ex.Message}";
            _log.Error(Error);
            return false;
        }
    }
}
