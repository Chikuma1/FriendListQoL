using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListQoL;

/// <summary>
/// Temporary calibration helper for mapping the "Remove from Friend List"
/// flow. Toggle with /flqol calibrate. Logs every UI event to the Dalamud
/// plugin log so we can see what happens when the game's remove entry is
/// clicked. The user should cancel at the confirmation dialog.
/// </summary>
public sealed class CalibrationService : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IAddonLifecycle _addonLifecycle;
    private readonly IContextMenu _contextMenu;

    public bool IsActive { get; private set; }

    public CalibrationService(IPluginLog log, IAddonLifecycle addonLifecycle, IContextMenu contextMenu)
    {
        _log = log;
        _addonLifecycle = addonLifecycle;
        _contextMenu = contextMenu;
    }

    public void SetActive(bool active)
    {
        if (active == IsActive)
            return;

        IsActive = active;
        if (active)
        {
            _log.Warning("[CALIBRATE] === Calibration logging ON ===");
            _log.Warning("[CALIBRATE] Steps: 1) Open game Friend List  2) Right-click a friend  3) Click 'Remove from Friend List'  4) Click 'No' at the confirmation  5) Run /flqol calibrate again to stop");
            foreach (AddonEvent evt in Enum.GetValues(typeof(AddonEvent)))
            {
                try { _addonLifecycle.RegisterListener(evt, OnAnyAddonEvent); }
                catch { /* some events may not support global listeners */ }
            }
            _contextMenu.OnMenuOpened += OnMenuOpened;
        }
        else
        {
            _log.Warning("[CALIBRATE] === Calibration logging OFF ===");
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

        _log.Warning($"[CALIBRATE] MenuOpened: addon='{args.AddonName}' menuType={args.MenuType} target=[{target}]");
    }

    private unsafe void OnAnyAddonEvent(AddonEvent type, AddonArgs args)
    {
        if (!IsActive)
            return;

        var stamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var msg = $"[CALIBRATE] {stamp} AddonEvent={type} addon='{args.AddonName}'";

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

        _log.Warning(msg);
    }

    public void Dispose() => SetActive(false);
}
