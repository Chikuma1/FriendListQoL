using System;
using System.IO;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListQoL;

/// <summary>
/// Temporary calibration helper for mapping the "Remove from Friend List"
/// flow. Toggle with /flqol calibrate. Writes every UI event to a dedicated
/// calibration.log in the plugin config directory (not the main Dalamud
/// log) and confirms state changes in-game via chat. The user should cancel
/// at the confirmation dialog.
/// </summary>
public sealed class CalibrationService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IContextMenu _contextMenu;
    private readonly IChatGui _chatGui;
    private readonly IGameGui _gameGui;
    private readonly StreamWriter _file;
    private readonly object _fileLock = new();

    public bool IsActive { get; private set; }

    public CalibrationService(IPluginLog log, IAddonLifecycle addonLifecycle, IContextMenu contextMenu, IChatGui chatGui, IGameGui gameGui, string configDirectory)
    {
        _log = log;
        _addonLifecycle = addonLifecycle;
        _contextMenu = contextMenu;
        _chatGui = chatGui;
        _gameGui = gameGui;
        var path = Path.Combine(configDirectory, "calibration.log");
        _file = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    private void Log(string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}";
        lock (_fileLock)
            _file.WriteLine(line);
    }

    private void Chat(string msg) =>
        _chatGui.Print(new XivChatEntry { Message = $"[FLQoL] {msg}" });

    public void SetActive(bool active)
    {
        if (active == IsActive)
            return;

        IsActive = active;
        if (active)
        {
            Log("=== Calibration logging ON ===");
            Log("Steps: 1) Open game Friend List  2) Right-click a friend  3) Click 'Remove from Friend List'  4) Click 'No' at the confirmation  5) Run /flqol calibrate again to stop");
            Chat("Calibration logging ON — writing to calibration.log in the plugin config folder.");
            _log.Warning("[FriendListQoL] Calibration logging ON (see calibration.log)");
            foreach (AddonEvent evt in Enum.GetValues(typeof(AddonEvent)))
            {
                try { _addonLifecycle.RegisterListener(evt, OnAnyAddonEvent); }
                catch { /* some events may not support global listeners */ }
            }
            _contextMenu.OnMenuOpened += OnMenuOpened;
        }
        else
        {
            Log("=== Calibration logging OFF ===");
            Chat("Calibration logging OFF. Send me the new lines from calibration.log.");
            _log.Warning("[FriendListQoL] Calibration logging OFF");
            foreach (AddonEvent evt in Enum.GetValues(typeof(AddonEvent)))
            {
                try { _addonLifecycle.UnregisterListener(evt, OnAnyAddonEvent); }
                catch { }
            }
            _contextMenu.OnMenuOpened -= OnMenuOpened;
        }
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!IsActive)
            return;

        string target;
        if (args.Target is MenuTargetDefault d)
            target = $"MenuTargetDefault name='{d.TargetName}' contentId={d.TargetContentId} objectId={d.TargetObjectId}";
        else
            target = args.Target?.GetType().Name ?? "null";

        Log($"MenuOpened: addon='{args.AddonName}' menuType={args.MenuType} target=[{target}]");
        LogAgentState("MenuOpened");
    }

    /// <summary>
    /// Dumps AgentFriendlist state: selected index and list info.
    /// This helps us correlate the right-click target with the agent's selection.
    /// </summary>
    private unsafe void LogAgentState(string context)
    {
        try
        {
            var agentModule = AgentModule.Instance();
            if (agentModule == null)
            {
                Log($"AgentState[{context}]: AgentModule is null");
                return;
            }

            var agent = agentModule->GetAgentByInternalId(AgentId.Friendlist);
            if (agent == null || agent == (AgentInterface*)nint.Zero)
            {
                Log($"AgentState[{context}]: AgentFriendlist not found");
                return;
            }

            var friendAgent = (AgentFriendlist*)agent;
            Log($"AgentState[{context}]: AgentFriendlist*={(nint)friendAgent:X} SelectedContentId={friendAgent->SelectedContentId} SelectedIndex={friendAgent->SelectedIndex}");
            
            // Try to get the FriendList addon and its list state
            var addonPtr = _gameGui.GetAddonByName("FriendList");
            if (addonPtr != nint.Zero)
            {
                var addon = (AddonFriendList*)addonPtr.Address;
                Log($"AgentState[{context}]: AddonFriendList*={(nint)addon:X}");
            }

            // Inspect AgentContext for menu event IDs
            LogContextMenuState(context, agentModule);
        }
        catch (Exception ex)
        {
            Log($"AgentState[{context}]: failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Inspects AgentContext's menu to find event IDs for menu items.
    /// The "Remove from Friend List" item has a specific event ID we need to capture.
    /// </summary>
    private unsafe void LogContextMenuState(string context, AgentModule* agentModule)
    {
        try
        {
            var ctxAgent = agentModule->GetAgentByInternalId(AgentId.Context);
            if (ctxAgent == null || ctxAgent == (AgentInterface*)nint.Zero)
                return;

            var ctx = (AgentContext*)ctxAgent;
            Log($"ContextState[{context}]: TargetContentId={ctx->TargetContentId} TargetName='{ctx->TargetName}'");
            Log($"ContextState[{context}]: YesNoTargetContentId={ctx->YesNoTargetContentId} YesNoEventId={ctx->YesNoEventId}");

            // Read the main context menu's event IDs
            // _eventIds is at offset 0x448, array of 34 ints (event IDs)
            var menu = &ctx->MainContextMenu;
            var eventCount = menu->CurrentEventIndex;
            Log($"ContextState[{context}]: Menu has {eventCount} items, CurrentEventId={menu->CurrentEventId}");
            
            // Read event IDs directly from memory (offset 0x448, 34 x int32)
            var eventIdsPtr = (int*)((byte*)menu + 0x448);
            for (int i = 0; i < eventCount && i < 34; i++)
            {
                var eventId = eventIdsPtr[i];
                Log($"ContextState[{context}]: MenuItem[{i}] EventId={eventId}");
            }
        }
        catch (Exception ex)
        {
            Log($"ContextState[{context}]: failed: {ex.GetType().Name}");
        }
    }

    private static bool IsRelevantAddon(string addonName) =>
        addonName.Contains("FriendList", StringComparison.OrdinalIgnoreCase)
        || addonName.Contains("SelectYesno", StringComparison.OrdinalIgnoreCase)
        || addonName.Contains("Context", StringComparison.OrdinalIgnoreCase);

    private unsafe void OnAnyAddonEvent(AddonEvent type, AddonArgs args)
    {
        if (!IsActive)
            return;

        // Only log addons involved in the remove flow — everything else is noise.
        // Skip per-frame update events to keep the log clean.
        if (!IsRelevantAddon(args.AddonName))
            return;
        if (type is AddonEvent.PreUpdate or AddonEvent.PostUpdate or AddonEvent.PreDraw or AddonEvent.PostDraw)
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var msg = $"{stamp} AddonEvent={type} addon='{args.AddonName}'";

        // Log agent state when FriendList receives events (the remove click goes through here)
        if (args.AddonName == "FriendList" && type is AddonEvent.PreReceiveEvent or AddonEvent.PostReceiveEvent)
        {
            LogAgentState($"ReceiveEvent-{type}");
        }

        // Capture SelectYesno dialog details — this is the removal confirmation.
        if (args.AddonName == "SelectYesno")
        {
            try
            {
                var addon = (AddonSelectYesno*)(nint)args.Addon;
                if (addon != null)
                {
                    var prompt = addon->PromptText != null
                        ? addon->PromptText->NodeText.ToString()
                        : "(null prompt)";
                    msg += $" PROMPT=\"{prompt}\"";

                    // Log first few AtkValues — they may carry the target context.
                    if (addon->AtkUnitBase.AtkValues != null)
                    {
                        msg += " AtkValues=[";
                        for (int i = 0; i < 4; i++)
                        {
                            var v = addon->AtkUnitBase.AtkValues[i];
                            msg += $"({v.Type}:{v.Int})";
                        }
                        msg += "]";
                    }
                }
            }
            catch (Exception ex)
            {
                msg += $" (inspect failed: {ex.GetType().Name})";
            }
        }

        Log(msg);
    }

    public void Dispose()
    {
        SetActive(false);
        lock (_fileLock)
        {
            _file.Flush();
            _file.Dispose();
        }
    }
}
