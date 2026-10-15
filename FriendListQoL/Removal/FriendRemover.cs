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
/// Two modes: GUIDED (default) — the user right-clicks → Remove per friend and
/// the plugin auto-confirms the dialog after the prompt-name safety check.
/// FULL-AUTO v2 (experimental, <c>/flqol fullauto</c>) — the plugin synthesizes
/// a right-click on the friend's list row via the list's own event handler,
/// letting the game's code open the context menu with the correct target, then
/// activates the Remove item. Validate with <c>/flqol synctest</c> first.
/// </summary>
public sealed unsafe class FriendRemover : IDisposable
{
    private enum Step
    {
        Idle,
        Triggering,
        WaitingForFriendList,
        WaitingForContextMenu,
        WaitingForConfirm,
        CoolingDown,
        Finished,
        Failed,
    }

    private const string FriendListAddonName = "FriendList";
    private const string ConfirmAddonName = "SelectYesno";
    private const string ContextMenuAddonName = "ContextMenu";

    /// <summary>How long to wait for the game's confirm dialog after triggering a removal.</summary>
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(8);

    /// <summary>How long to wait for the context menu after a synthesized right-click.</summary>
    private static readonly TimeSpan ContextMenuTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGameGui _gameGui;
    private readonly ICommandManager _commands;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly Configuration _config;

    private readonly Queue<FriendEntry> _queue = new();
    private readonly List<string> _removedNames = new();

    private Step _step = Step.Idle;
    private FriendEntry? _current;
    private DateTime _nextActionAt = DateTime.MinValue;
    private DateTime _confirmDeadline = DateTime.MinValue;
    private DateTime _contextMenuDeadline = DateTime.MinValue;
    private DateTime _simConfirmAt = DateTime.MinValue;
    private bool _confirmClicked;
    private int _syncTestButtonIndex;
    private byte _lastSynthButtonId = FullAutoV2.RightButtonId;
    private bool _capturingClick;
    private DateTime _friendListDeadline = DateTime.MinValue;

    /// <summary>How long to wait for the Friend List to open after /friendlist.</summary>
    private static readonly TimeSpan FriendListTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When true, the v2 flow stops after the context menu opens (used by
    /// <c>/flqol synctest</c>): it verifies the menu target but never clicks
    /// Remove. Safe calibration with zero removal risk.
    /// </summary>
    public bool SyncTestMode { get; set; }

    /// <summary>Reports synctest progress to the caller (chat).</summary>
    public Action<string>? SyncTestReport { get; set; }

    public bool IsRunning => _step is Step.Triggering or Step.WaitingForFriendList or Step.WaitingForConfirm or Step.CoolingDown or Step.WaitingForContextMenu;
    public bool HasFinished => _step == Step.Finished;
    public bool HasFailed => _step == Step.Failed;

    /// <summary>
    /// True when we're waiting for the user to manually trigger the remove
    /// (right-click → Remove from Friend List) in guided mode.
    /// </summary>
    public bool IsWaitingForUser => _step == Step.WaitingForConfirm && !SimulateRemovals && !_confirmClicked && !AttemptFullAutomation;

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

    public FriendRemover(IPluginLog log, IFramework framework, IGameGui gameGui, ICommandManager commands, IAddonLifecycle addonLifecycle, Configuration config)
    {
        _log = log;
        _framework = framework;
        _gameGui = gameGui;
        _commands = commands;
        _addonLifecycle = addonLifecycle;
        _config = config;

        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, ConfirmAddonName, OnConfirmDialogOpened);
        _addonLifecycle.RegisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuOpened);
        _framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, ConfirmAddonName, OnConfirmDialogOpened);
        _addonLifecycle.UnregisterListener(AddonEvent.PostSetup, ContextMenuAddonName, OnContextMenuOpened);
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
        SyncTestMode = false;

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
                    if (AttemptFullAutomation && !SimulateRemovals)
                    {
                        // v2: TriggerFullAutomationV2 already set _contextMenuDeadline.
                        _step = Step.WaitingForContextMenu;
                    }
                    else
                    {
                        _step = Step.WaitingForConfirm;
                        _confirmDeadline = now + ConfirmTimeout;
                    }
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
                    // Full-auto v2: the dialog should already be on its way.
                    Fail($"The game's confirmation dialog never appeared for {_current?.Name}.");
                }
                StateChanged?.Invoke();
                break;

            case Step.WaitingForFriendList:
                if (FullAutoV2.GetFriendListAddon(_gameGui) != null)
                {
                    _log.Info("[FAV2] Friend List opened; proceeding with synctest click.");
                    TriggerSyncTestClick();
                }
                else if (now > _friendListDeadline)
                {
                    if (SyncTestMode)
                    {
                        ReportSyncTest("The Friend List didn't open after /friendlist. Open it manually and run synctest again.");
                        SyncTestMode = false;
                    }
                    Fail("The Friend List didn't open.");
                }
                StateChanged?.Invoke();
                break;
            case Step.WaitingForContextMenu:
                if (now > _contextMenuDeadline)
                {
                    if (SyncTestMode && _syncTestButtonIndex + 1 < FullAutoV2.CalibrationButtonIds.Length)
                    {
                        // Calibration: this button id didn't open the menu — try the next one.
                        _syncTestButtonIndex++;
                        var triedId = _lastSynthButtonId;
                        var nextId = FullAutoV2.CalibrationButtonIds[_syncTestButtonIndex];
                        ReportSyncTest($"ButtonId={triedId} didn't open the menu. Trying ButtonId={nextId}...");
                        if (_current != null && TriggerFullAutomationV2(_current, nextId))
                        {
                            _step = Step.WaitingForContextMenu;
                            break;
                        }
                        ReportSyncTest($"Re-trigger failed: {Error}");
                    }
                    if (SyncTestMode)
                    {
                        ReportSyncTest($"No context menu opened for {_current?.Name} within {ContextMenuTimeout.TotalSeconds}s. The right-click synthesis parameters need adjusting — see the dalamud log.");
                        SyncTestMode = false;
                    }
                    Fail($"The context menu never opened for {_current?.Name} after the synthesized right-click.");
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

    private void ReportSyncTest(string message)
    {
        _log.Info($"[SYNCTEST] {message}");
        try { SyncTestReport?.Invoke(message); } catch { /* never break the flow for chat */ }
    }

    /// <summary>
    /// Starts a safe single-shot calibration: finds the friend's row and
    /// synthesizes a right-click, then reports whether the context menu
    /// opened with the right target. Never clicks Remove — zero removal risk.
    /// The Friend List window must be open. Favorites are NOT filtered here
    /// on purpose: the target is never removed.
    /// </summary>
    public void BeginSyncTest(FriendEntry entry)
    {
        if (IsRunning)
            return;
        _queue.Clear();
        _removedNames.Clear();
        Error = null;
        _queue.Enqueue(entry);
        TotalCount = 1;
        SyncTestMode = true;
        _syncTestButtonIndex = 0;
        _current = _queue.Dequeue();
        _confirmClicked = false;
        ReportSyncTest($"Starting synctest for '{entry.Name}' (contentId={entry.ContentId}).");

        if (FullAutoV2.GetFriendListAddon(_gameGui) == null)
        {
            // Open the Friend List via the game's own command, then wait for it.
            _commands.ProcessCommand("/friendlist");
            _friendListDeadline = DateTime.UtcNow + FriendListTimeout;
            _step = Step.WaitingForFriendList;
            ReportSyncTest("Friend List wasn't open — opening it via /friendlist...");
            StateChanged?.Invoke();
            return;
        }

        TriggerSyncTestClick();
        StateChanged?.Invoke();
    }

    /// <summary>Runs the v2 click synthesis for the current synctest entry.</summary>
    private void TriggerSyncTestClick()
    {
        if (_current == null)
        {
            SyncTestMode = false;
            Fail("Synctest lost its target entry.");
            return;
        }
        if (TriggerFullAutomationV2(_current))
        {
            _step = Step.WaitingForContextMenu;
            _contextMenuDeadline = DateTime.UtcNow + ContextMenuTimeout;
        }
        else
        {
            SyncTestMode = false;
            Fail(Error ?? "Failed to trigger the synthesized right-click.");
        }
    }

    /// <summary>
    /// Starts click-capture calibration: the user right-clicks any friend row
    /// (the real thing, which is known to work) and the plugin measures where
    /// the context menu opened versus where it computes the row to be. The
    /// difference is saved as the click offset — no rebuilds needed.
    /// </summary>
    public void BeginClickCapture()
    {
        if (FullAutoV2.GetFriendListAddon(_gameGui) == null)
            _commands.ProcessCommand("/friendlist");
        _capturingClick = true;
        ReportSyncTest("Right-click any friend row in your Friend List — I'll measure where the menu opened.");
        _log.Info("[FAV2] Click capture armed; waiting for a real right-click.");
    }

    /// <summary>Clears the calibrated click offset back to zero.</summary>
    public void ResetClickOffset()
    {
        _config.ClickOffsetX = 0;
        _config.ClickOffsetY = 0;
        _config.Save();
        FullAutoV2.ClickOffsetX = 0;
        FullAutoV2.ClickOffsetY = 0;
        ReportSyncTest("Click offset reset to (0,0).");
    }

    /// <summary>
    /// Handles a real right-click while capture is armed: reads the context
    /// menu's screen position (it opens at the cursor) and the targeted
    /// friend, diffs against our computed row position, and saves the offset.
    /// </summary>
    private void HandleClickCapture(AddonArgs args)
    {
        _capturingClick = false;
        try
        {
            var agentModule = AgentModule.Instance();
            string targetName = string.Empty;
            if (agentModule != null)
            {
                var ctxAgent = agentModule->GetAgentByInternalId(AgentId.Context);
                if (ctxAgent != null && ctxAgent != (AgentInterface*)nint.Zero)
                    targetName = ((AgentContext*)ctxAgent)->TargetName.ToString();
            }

            var menu = (AtkUnitBase*)args.Addon.Address;
            short menuX = 0, menuY = 0;
            if (menu != null && menu->RootNode != null)
            {
                menuX = (short)menu->RootNode->X;
                menuY = (short)menu->RootNode->Y;
            }

            var addon = FullAutoV2.GetFriendListAddon(_gameGui);
            int rowIndex = -1;
            short calcX = 0, calcY = 0;
            if (addon != null && !string.IsNullOrEmpty(targetName))
            {
                rowIndex = FullAutoV2.FindFriendRowIndex(addon, targetName, _log);
                if (rowIndex >= 0)
                {
                    var renderer = FullAutoV2.FindRendererForListIndex(addon->FriendList, rowIndex);
                    AtkResNode* targetNode = null;
                    if (renderer != null && renderer->OwnerNode != null)
                        targetNode = (AtkResNode*)renderer->OwnerNode;
                    (calcX, calcY) = FullAutoV2.GetNodeScreenCenter(targetNode);
                }
            }

            _log.Info($"[FAV2] Click capture: target='{targetName}' row={rowIndex} menuPos=({menuX},{menuY}) computedPos=({calcX},{calcY}).");

            if (string.IsNullOrEmpty(targetName) || rowIndex < 0)
            {
                ReportSyncTest($"Capture got target '{targetName}' but couldn't map it to a friend-list row. Try again, right-clicking a row directly.");
                return;
            }

            var offX = (short)(menuX - calcX);
            var offY = (short)(menuY - calcY);
            _config.ClickOffsetX = offX;
            _config.ClickOffsetY = offY;
            _config.Save();
            FullAutoV2.ClickOffsetX = offX;
            FullAutoV2.ClickOffsetY = offY;

            ReportSyncTest($"Captured '{targetName}': menu opened at ({menuX},{menuY}), we computed ({calcX},{calcY}). Click offset saved as ({offX},{offY}). Run synctest again.");
        }
        catch (Exception ex)
        {
            _log.Error($"[FAV2] Click capture failed: {ex.GetType().Name}: {ex.Message}");
            ReportSyncTest($"Click capture failed: {ex.Message}");
        }
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
    /// When true, attempts full automation via UI event synthesis (v2): the
    /// plugin right-clicks the friend's list row through the game's own event
    /// pipeline instead of asking the user to do it. When false (default),
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
    /// Fired when the game's context menu opens. In full-auto v2 mode, verifies
    /// the menu target is the queued friend, then activates the Remove item.
    /// In synctest mode, reports the target and stops — never removes.
    /// <para/>
    /// SAFETY: the menu must target the queued friend's name; otherwise we
    /// refuse to click Remove, exactly like the SelectYesno prompt check.
    /// </summary>
    private unsafe void OnContextMenuOpened(AddonEvent type, AddonArgs args)
    {
        if (_capturingClick)
        {
            HandleClickCapture(args);
            return;
        }
        if (_step != Step.WaitingForContextMenu || _current == null)
            return;
        if (!AttemptFullAutomation && !SyncTestMode)
            return;

        try
        {
            var agentModule = AgentModule.Instance();
            string targetName = string.Empty;
            if (agentModule != null)
            {
                var ctxAgent = agentModule->GetAgentByInternalId(AgentId.Context);
                if (ctxAgent != null && ctxAgent != (AgentInterface*)nint.Zero)
                    targetName = ((AgentContext*)ctxAgent)->TargetName.ToString();
            }

            _log.Info($"[FAV2] Context menu opened. AgentContext target='{targetName}', expected='{_current.Name}'.");
            var firstName = _current.Name.Split(' ')[0];
            if (string.IsNullOrEmpty(targetName) ||
                !targetName.Contains(firstName, StringComparison.OrdinalIgnoreCase))
            {
                var msg = $"Context menu target '{targetName}' doesn't match expected friend '{_current.Name}'. NOT clicking Remove.";
                _log.Warning($"[FAV2] {msg}");
                if (SyncTestMode)
                {
                    ReportSyncTest(msg + " (synthesis reached the menu but targeted the wrong row — check the row mapping.)");
                    SyncTestMode = false;
                }
                Fail(msg);
                return;
            }

            if (SyncTestMode)
            {
                ReportSyncTest($"SUCCESS: context menu opened with target '{targetName}' (ButtonId={_lastSynthButtonId}) — the synthesized right-click works! No removal performed (synctest only).");
                SyncTestMode = false;
                _current = null;
                _step = Step.Finished;
                _log.Info("[FAV2] Synctest finished successfully.");
                StateChanged?.Invoke();
                return;
            }

            var menuAddon = (AtkUnitBase*)(nint)args.Addon;
            if (!FullAutoV2.ActivateRemoveMenuItem(menuAddon, _log))
            {
                Fail($"Couldn't activate '{FullAutoV2.RemoveMenuItemText}' in the context menu for {_current.Name}.");
                return;
            }

            _step = Step.WaitingForConfirm;
            _confirmDeadline = DateTime.UtcNow + ConfirmTimeout;
            _log.Info($"[FAV2] Remove item activated for {_current.Name}; waiting for the confirmation dialog.");
        }
        catch (Exception ex)
        {
            Fail($"Context menu handling failed: {ex.Message}");
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Attempts full automation: sets the agent selection to target the friend,
    /// then triggers the remove confirmation dialog programmatically.
    /// ⚠ EXPERIMENTAL: Test on one friend first with auto-confirm DISABLED!
    /// </summary>
    private unsafe bool TriggerFullAutomation(FriendEntry entry)
    {
        // v2: UI event synthesis. The v1.0.11 memory-write approach
        // (AgentFriendlist.SelectedContentId) is dead — the game's own
        // right-click handling stomps it (see calibration-5.log).
        return TriggerFullAutomationV2(entry);
    }

    /// <summary>
    /// Full-auto v2: find the friend's row in the FriendList UI, synthesize a
    /// right-click on it so the game's own code opens the context menu with
    /// the correct target, then let <see cref="OnContextMenuOpened"/> take it
    /// from there. Guided mode is untouched — this only runs when
    /// <see cref="AttemptFullAutomation"/> is on.
    /// </summary>
    private unsafe bool TriggerFullAutomationV2(FriendEntry entry, byte? buttonIdOverride = null)
    {
        try
        {
            var addon = FullAutoV2.GetFriendListAddon(_gameGui);
            if (addon == null)
            {
                Error = "FriendList addon not available.";
                return false;
            }

            var list = addon->FriendList;
            if (list == null)
            {
                Error = "FriendList list component not available.";
                return false;
            }

            var rowIndex = FullAutoV2.FindFriendRowIndex(addon, entry.Name, _log);
            if (rowIndex < 0)
            {
                Error = $"Couldn't find '{entry.Name}' in the Friend List UI. They may be scrolled out of view — scroll the list and try again.";
                return false;
            }

            FullAutoV2.EnsureVisible(list, rowIndex, _log);

            var buttonId = buttonIdOverride
                ?? (SyncTestMode ? FullAutoV2.CalibrationButtonIds[_syncTestButtonIndex] : FullAutoV2.RightButtonId);
            _lastSynthButtonId = buttonId;
            _log.Info($"[FAV2] Synthesizing right-click on row {rowIndex} for {entry.Name}.");
            FullAutoV2.SynthesizeRightClick(list, rowIndex, buttonId, _log);

            // The result is verified when the context menu opens (or times out).
            _contextMenuDeadline = DateTime.UtcNow + ContextMenuTimeout;
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Full-auto v2 failed: {ex.Message}";
            _log.Error(Error);
            return false;
        }
    }
}
