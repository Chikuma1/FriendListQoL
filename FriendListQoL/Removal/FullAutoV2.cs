using System;
using System.Text;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace FriendListQoL.Removal;

/// <summary>
/// Experimental full-automation via UI event synthesis (v2).
/// <para/>
/// Unlike the v1.0.11 attempt — which wrote
/// <c>AgentFriendlist.SelectedContentId</c> directly and watched the game's own
/// right-click handling stomp it (see calibration-5.log) — this drives the
/// game's own UI event pipeline instead:
/// <list type="number">
/// <item>Find the friend's row in the FriendList <c>AtkComponentList</c>.</item>
/// <item>Call the list's own <c>HandleMouseUp</c> with right-button mouse data,
/// so the game's code opens the context menu with the correct target.</item>
/// <item>Activate the "Remove from Friend List" item in the context menu via
/// <c>DispatchItemEvent</c>.</item>
/// </list>
/// No memory writes anywhere in the chain, so there is nothing for the game
/// to overwrite. The existing SelectYesno prompt-name safety check remains
/// the backstop before any Yes is clicked.
/// <para/>
/// CALIBRATION STATUS: the right-click synthesis parameters (mouse ButtonId,
/// eventParam, AtkEvent construction) are best-effort and MUST be validated
/// in-game via <c>/flqol synctest</c> before trusting a real purge.
/// </summary>
public static unsafe class FullAutoV2
{
    public const string FriendListAddonName = "FriendList";
    public const string ContextMenuAddonName = "ContextMenu";
    public const string RemoveMenuItemText = "Remove from Friend List";

    /// <summary>
    /// Default guess for the right mouse button id in <c>AtkMouseData</c>.
    /// Synctest tries <see cref="CalibrationButtonIds"/> in order and reports
    /// which one opens the context menu.
    /// </summary>
    public const byte RightButtonId = 1;

    /// <summary>Button ids the synctest calibrates through, in try order.</summary>
    public static readonly byte[] CalibrationButtonIds = { 1, 2 };

    /// <summary>
    /// Computes the screen-space center of a node by walking up the parent
    /// chain. A child point (px,py) in node N's local space maps to
    /// N's parent space as (N.X + px * N.ScaleX, N.Y + py * N.ScaleY).
    /// Rotation is ignored (zero for list rows).
    /// </summary>
    public static (short X, short Y) GetNodeScreenCenter(AtkResNode* node)
    {
        if (node == null)
            return (0, 0);
        float lx = node->Width / 2f;
        float ly = node->Height / 2f;
        var n = node;
        while (n->ParentNode != null)
        {
            lx = n->X + lx * n->ScaleX;
            ly = n->Y + ly * n->ScaleY;
            n = n->ParentNode;
        }
        return ((short)lx, (short)ly);
    }

    public static AddonFriendList* GetFriendListAddon(IGameGui gameGui)
    {
        var ptr = gameGui.GetAddonByName(FriendListAddonName);
        return ptr == nint.Zero ? null : (AddonFriendList*)ptr.Address;
    }

    /// <summary>
    /// Finds the list index of the row displaying the given friend name.
    /// Returns -1 when the FriendList addon or the row can't be found.
    /// Logs every visible row for calibration.
    /// </summary>
    public static int FindFriendRowIndex(AddonFriendList* addon, string name, IPluginLog log)
    {
        var list = addon->FriendList;
        if (list == null)
        {
            log.Warning("[FAV2] AddonFriendList.FriendList component is null.");
            return -1;
        }

        var rendererCount = list->AllocatedItemRendererListLength;
        log.Info($"[FAV2] Scanning {rendererCount} allocated renderers for '{name}' (ListLength={list->ListLength}).");
        for (var i = 0; i < rendererCount; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null)
                continue;
            var text = GetRendererText(renderer);
            log.Info($"[FAV2] renderer[{i}] listIndex={renderer->ListItemIndex} text='{text}'");
            if (!string.IsNullOrEmpty(text) && text.Contains(name, StringComparison.OrdinalIgnoreCase))
                return renderer->ListItemIndex;
        }

        log.Warning($"[FAV2] No row found for '{name}'. The friend may be scrolled out of view.");
        return -1;
    }

    /// <summary>
    /// Ensures the given list index is scrolled into view.
    /// </summary>
    public static void EnsureVisible(AtkComponentList* list, int listIndex, IPluginLog log)
    {
        if (!list->IsItemVisible(listIndex))
        {
            log.Info($"[FAV2] Scrolling to item {listIndex}.");
            list->ScrollToItem((short)listIndex);
        }
    }

    /// <summary>
    /// Synthesizes a full mouse-button click (down then up) on the given list
    /// item by calling the list's own mouse handlers with mouse data aimed at
    /// the row's screen-space center. A real click is down+up; the context menu
    /// may open on either. If the parameters are right, the game's code opens
    /// the context menu with this row's friend as the target — exactly as a
    /// real click. Returns true if either handler reported handling it.
    /// Any exception is logged, never thrown.
    /// </summary>
    public static bool SynthesizeRightClick(AtkComponentList* list, int listIndex, byte buttonId, IPluginLog log)
    {
        try
        {
            var renderer = FindRendererForListIndex(list, listIndex);
            AtkResNode* targetNode = null;
            if (renderer != null && renderer->OwnerNode != null)
                targetNode = (AtkResNode*)renderer->OwnerNode;

            var (posX, posY) = GetNodeScreenCenter(targetNode);

            var mouseData = stackalloc AtkEventData.AtkMouseData[1];
            mouseData->PosX = posX;
            mouseData->PosY = posY;
            mouseData->WheelDirection = 0;
            mouseData->ButtonId = buttonId;
            mouseData->Modifier = ModifierFlag.None;

            var atkEvent = stackalloc AtkEvent[1];
            atkEvent->Node = targetNode;
            atkEvent->Target = (AtkEventTarget*)targetNode;
            atkEvent->Listener = null;
            atkEvent->Param = 0;
            atkEvent->NextEvent = null;

            log.Info($"[FAV2] HandleMouseDown(listIndex={listIndex}, ButtonId={buttonId}, pos=({posX},{posY}), targetNode={(nint)targetNode:X})...");
            var downResult = list->HandleMouseDown(atkEvent, mouseData, 0, listIndex);
            log.Info($"[FAV2] HandleMouseDown returned {downResult}.");
            log.Info($"[FAV2] HandleMouseUp(listIndex={listIndex}, ButtonId={buttonId}, pos=({posX},{posY}), targetNode={(nint)targetNode:X})...");
            var upResult = list->HandleMouseUp(atkEvent, mouseData, 0, listIndex);
            log.Info($"[FAV2] HandleMouseUp returned {upResult}.");
            return downResult || upResult;
        }
        catch (Exception ex)
        {
            log.Error($"[FAV2] SynthesizeRightClick threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Finds the "Remove from Friend List" item in an open context menu addon
    /// and activates it via <c>DispatchItemEvent</c> (a synthetic left-click).
    /// Returns false when the menu, its list, or the item can't be found.
    /// </summary>
    public static bool ActivateRemoveMenuItem(AtkUnitBase* menuAddon, IPluginLog log)
    {
        try
        {
            var list = FindFirstListComponent(menuAddon, log);
            if (list == null)
            {
                log.Warning("[FAV2] No AtkComponentList found in the context menu addon.");
                return false;
            }

            var index = FindListItemIndexByText(list, RemoveMenuItemText, log);
            if (index < 0)
            {
                log.Warning($"[FAV2] '{RemoveMenuItemText}' not found in the context menu.");
                return false;
            }

            log.Info($"[FAV2] DispatchItemEvent(index={index}, ListItemClick) for '{RemoveMenuItemText}'.");
            list->DispatchItemEvent(index, AtkEventType.ListItemClick);
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[FAV2] ActivateRemoveMenuItem threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Finds the renderer currently showing the given list index, or null.</summary>
    public static AtkComponentListItemRenderer* FindRendererForListIndex(AtkComponentList* list, int listIndex)
    {
        for (var i = 0; i < list->AllocatedItemRendererListLength; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer != null && renderer->ListItemIndex == listIndex)
                return renderer;
        }
        return null;
    }

    /// <summary>
    /// Finds a list item's index by matching rendered text. Returns -1 if none match.
    /// </summary>
    public static int FindListItemIndexByText(AtkComponentList* list, string text, IPluginLog log)
    {
        for (var i = 0; i < list->AllocatedItemRendererListLength; i++)
        {
            var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
            if (renderer == null)
                continue;
            var itemText = GetRendererText(renderer);
            log.Info($"[FAV2] menu renderer[{i}] listIndex={renderer->ListItemIndex} text='{itemText}'");
            if (!string.IsNullOrEmpty(itemText) && itemText.Contains(text, StringComparison.OrdinalIgnoreCase))
                return renderer->ListItemIndex;
        }
        return -1;
    }

    /// <summary>
    /// Walks an addon's node tree and returns the first list component found.
    /// </summary>
    public static AtkComponentList* FindFirstListComponent(AtkUnitBase* addon, IPluginLog log)
    {
        var root = addon->RootNode;
        if (root == null)
            return null;
        return FindListInSubtree(root);
    }

    private static AtkComponentList* FindListInSubtree(AtkResNode* node)
    {
        if (node == null)
            return null;

        if (node->Type == NodeType.Component)
        {
            var compNode = (AtkComponentNode*)node;
            var candidate = (AtkComponentList*)compNode->Component;
            if (IsPlausibleList(candidate))
                return candidate;
        }

        var child = node->ChildNode;
        while (child != null)
        {
            var found = FindListInSubtree(child);
            if (found != null)
                return found;
            child = child->NextSiblingNode;
        }
        return null;
    }

    /// <summary>
    /// Heuristic check that a component pointer is really an AtkComponentList.
    /// The structs expose no type tag on AtkComponentBase, so we sanity-check
    /// the list fields instead. Generous bounds — synctest logging shows what
    /// was actually found.
    /// </summary>
    private static bool IsPlausibleList(AtkComponentList* candidate)
    {
        if (candidate == null)
            return false;
        try
        {
            var length = candidate->ListLength;
            var allocated = candidate->AllocatedItemRendererListLength;
            return length > 0 && length <= 500
                && allocated > 0 && allocated <= 500
                && candidate->ItemRendererList != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Concatenates all text nodes under a list item renderer.</summary>
    public static string GetRendererText(AtkComponentListItemRenderer* renderer)
    {
        if (renderer == null)
            return string.Empty;
        var sb = new StringBuilder();
        // Path 1: the OwnerNode subtree (component root in the tree).
        if (renderer->OwnerNode != null)
            CollectText((AtkResNode*)renderer->OwnerNode, sb);
        // Path 2: the component's own ULD node list. For list item renderers
        // the text parts live here; the OwnerNode subtree walk alone was
        // returning empty text for visibly-rendered rows (v1.0.14 synctest).
        var uld = renderer->UldManager;
        if (uld.NodeList != null)
        {
            for (var i = 0; i < uld.NodeListCount; i++)
                CollectText(uld.NodeList[i], sb);
        }
        return sb.ToString();
    }

    private static void CollectText(AtkResNode* node, StringBuilder sb)
    {
        if (node == null)
            return;
        if (node->Type == NodeType.Text)
        {
            var textNode = (AtkTextNode*)node;
            var s = textNode->NodeText.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                if (sb.Length > 0)
                    sb.Append('|');
                sb.Append(s);
            }
        }
        var child = node->ChildNode;
        while (child != null)
        {
            CollectText(child, sb);
            child = child->NextSiblingNode;
        }
    }
}
