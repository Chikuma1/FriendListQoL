using System;
using System.IO;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
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
    private readonly StreamWriter _file;
    private readonly object _fileLock = new();

    public bool IsActive { get; private set; }

    public CalibrationService(IPluginLog log, IAddonLifecycle addonLifecycle, IContextMenu contextMenu, IChatGui chatGui, string configDirectory)
    {
        _log = log;
        _addonLifecycle = addonLifecycle;
        _contextMenu = contextMenu;
        _chatGui = chatGui;
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
        if (!IsRelevantAddon(args.AddonName))
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var msg = $"{stamp} AddonEvent={type} addon='{args.AddonName}'";

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
