using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FriendListQoL.Models;

namespace FriendListQoL.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;

    private string _search = string.Empty;
    private bool _purgeArmed;
    private string _newCategoryName = string.Empty;
    private ulong _newCategoryFor; // contentId currently creating a category for (0 = none)

    private static readonly Vector4 FavStarColor = new(1f, 0.82f, 0.25f, 1f);
    private static readonly Vector4 OnlineColor = new(0.35f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 OfflineColor = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 DangerColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 TestColor = new(1f, 0.72f, 0.25f, 1f);

    public MainWindow(Plugin plugin)
        : base("FriendListQoL###FriendListQoLMain", ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        Size = new Vector2(680, 720);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var cache = _plugin.FriendCache;
        var remover = _plugin.FriendRemover;
        var config = _plugin.Configuration;

        // ── Toolbar ──────────────────────────────────────────────
        ImGui.SetNextItemWidth(-260);
        ImGui.InputTextWithHint("###search", "Search name or world...", ref _search, 64);
        ImGui.SameLine();
        if (ImGui.Button("Refresh", new Vector2(110, 0)))
        {
            if (_plugin.TestMode)
                _plugin.FriendCache.GenerateTestFriends();
            else
                _plugin.FriendCache.RequestRefresh();
        }
        ImGui.SameLine();
        var testLabel = _plugin.TestMode ? "Exit test" : "Test mode";
        if (ImGui.Button(testLabel, new Vector2(110, 0)))
            _plugin.TestMode = !_plugin.TestMode;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Toggle 60 fake friends to test with. Nothing real is touched.");

        if (_plugin.TestMode)
        {
            ImGui.TextColored(TestColor,
                "TEST MODE — fake friends only. Favorites, sorting and purges are all simulated.");
            ImGui.Separator();
        }

        var all = cache.Friends;
        var favorites = all.Where(e => config.IsFavorite(e.ContentId))
                           .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                           .ToList();
        // Non-favorites, for the purge panel (selection state lives on the entries).
        var rest = all.Where(e => !config.IsFavorite(e.ContentId))
                      .Where(e => MatchesSearch(e, _search))
                      .OrderByDescending(e => e.IsOnline)
                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();

        ImGui.TextDisabled($"{favorites.Count} favorites  •  {config.Categories.Count} categories  •  {all.Count} friends total");
        ImGui.Separator();

        // ── Purge ────────────────────────────────────────────────
        DrawPurgePanel(remover, rest);
        ImGui.Separator();

        // ── Favorites: always pinned on top ──────────────────────
        ImGui.TextColored(FavStarColor, "★ Favorites (pinned)");
        if (favorites.Count == 0)
        {
            ImGui.TextDisabled("No favorites yet — star anyone below, or right-click them in the game's friend list.");
        }
        else
        {
            DrawFriendTable(favorites.Where(e => MatchesSearch(e, _search)).ToList(), showCheckbox: false);
        }

        ImGui.Spacing();
        ImGui.Separator();

        // ── Custom categories (collapsible) ──────────────────────
        for (var i = 0; i < config.Categories.Count; i++)
        {
            var cat = config.Categories[i];
            var members = rest.Where(e => cat.MemberContentIds.Contains(e.ContentId)).ToList();
            DrawCategorySection(cat, i, members);
        }

        if (config.Categories.Count > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
        }

        // ── Everyone else ────────────────────────────────────────
        var categorized = new HashSet<ulong>(config.Categories.SelectMany(c => c.MemberContentIds));
        var uncategorized = rest.Where(e => !categorized.Contains(e.ContentId)).ToList();
        ImGui.Text($"Everyone else ({uncategorized.Count})");
        if (uncategorized.Count == 0 && all.Count > 0)
        {
            ImGui.TextDisabled("Nobody here — everyone's favorited, categorized, or filtered out.");
        }
        else if (all.Count == 0)
        {
            ImGui.TextDisabled("Friend list is empty. Log in, open the game's Friend List once, then hit Refresh.");
        }
        else
        {
            DrawFriendTable(uncategorized, showCheckbox: true);
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawManageCategories();
    }

    private void DrawCategorySection(FriendCategory cat, int index, List<FriendEntry> members)
    {
        var config = _plugin.Configuration;
        var dir = cat.Collapsed ? ImGuiDir.Right : ImGuiDir.Down;
        if (ImGui.ArrowButton($"###catcol{index}", dir))
        {
            cat.Collapsed = !cat.Collapsed;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.Text($"{cat.Name} ({members.Count})");

        if (cat.Collapsed)
            return;

        if (members.Count == 0)
            ImGui.TextDisabled("Empty — use the … button on a friend to add them here.");
        else
            DrawFriendTable(members, showCheckbox: true);
    }

    private void DrawManageCategories()
    {
        var config = _plugin.Configuration;
        if (!ImGui.CollapsingHeader("Manage categories"))
            return;

        FriendCategory? toDelete = null;
        for (var i = 0; i < config.Categories.Count; i++)
        {
            var cat = config.Categories[i];
            ImGui.SetNextItemWidth(240);
            var name = cat.Name;
            if (ImGui.InputText($"###catname{i}", ref name, 64))
                cat.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit())
                config.Save();
            ImGui.SameLine();
            if (ImGui.Button($"Delete###catdel{i}"))
                toDelete = cat;
        }
        if (toDelete != null)
        {
            config.Categories.Remove(toDelete);
            config.Save();
        }
        if (ImGui.Button("New category"))
        {
            config.Categories.Add(new FriendCategory { Name = $"Category {config.Categories.Count + 1}" });
            config.Save();
        }
    }

    private void DrawPurgePanel(Removal.FriendRemover remover, List<FriendEntry> rest)
    {
        if (remover.IsRunning)
        {
            var done = remover.RemovedCount;
            var total = remover.TotalCount;
            ImGui.Text($"Purging… {done}/{total}");
            ImGui.ProgressBar(total == 0 ? 0 : (float)done / total, new Vector2(-120, 0));
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
                remover.Cancel();
            if (!string.IsNullOrEmpty(remover.CurrentName))
                ImGui.TextDisabled($"Removing: {remover.CurrentName}");
            return;
        }

        if (remover.HasFinished && remover.TotalCount > 0)
        {
            ImGui.TextColored(OnlineColor, $"Purge finished — removed {remover.RemovedCount} of {remover.TotalCount}.");
            ImGui.SameLine();
            if (ImGui.Button("Dismiss"))
                remover.Reset();
            return;
        }

        if (remover.HasFailed)
        {
            ImGui.TextColored(DangerColor, $"Purge stopped: {remover.Error}");
            ImGui.TextDisabled($"Removed {remover.RemovedCount} of {remover.TotalCount} before it stopped.");
            ImGui.SameLine();
            if (ImGui.Button("Dismiss"))
                remover.Reset();
            ImGui.Separator();
        }

        if (_purgeArmed)
        {
            var selected = rest.Where(e => e.SelectedForRemoval).ToList();
            var action = _plugin.TestMode ? "SIMULATE removing" : "Remove";
            ImGui.TextColored(DangerColor,
                $"{action} {selected.Count} friends? Favorites are never touched.");
            ImGui.TextDisabled("Preview: " + string.Join(", ", selected.Take(6).Select(e => e.Name)) +
                               (selected.Count > 6 ? $" (+{selected.Count - 6} more)" : ""));
            var confirmLabel = _plugin.TestMode ? "Yes, simulate it" : "Yes, remove them all";
            if (ImGui.Button(confirmLabel, new Vector2(200, 0)))
            {
                _purgeArmed = false;
                remover.Start(selected);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new Vector2(110, 0)))
                _purgeArmed = false;
            return;
        }

        var selectedCount = rest.Count(e => e.SelectedForRemoval);
        if (ImGui.Button("Select all non-favorites"))
            foreach (var e in rest) e.SelectedForRemoval = true;
        ImGui.SameLine();
        if (ImGui.Button("Clear selection"))
            foreach (var e in rest) e.SelectedForRemoval = false;
        ImGui.SameLine();
        ImGui.BeginDisabled(selectedCount == 0);
        if (ImGui.Button($"Remove selected ({selectedCount})", new Vector2(200, 0)))
            _purgeArmed = true;
        ImGui.EndDisabled();
    }

    private void DrawFriendTable(List<FriendEntry> entries, bool showCheckbox)
    {
        var config = _plugin.Configuration;
        var columnCount = showCheckbox ? 7 : 6;
        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("###friends", columnCount, flags))
            return;

        if (showCheckbox)
            ImGui.TableSetupColumn("##sel", ImGuiTableColumnFlags.WidthFixed, 28);
        ImGui.TableSetupColumn("##star", ImGuiTableColumnFlags.WidthFixed, 30);
        ImGui.TableSetupColumn("Name");
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 92);
        ImGui.TableSetupColumn("Job", ImGuiTableColumnFlags.WidthFixed, 46);
        ImGui.TableSetupColumn("Seen", ImGuiTableColumnFlags.WidthFixed, 74);
        ImGui.TableSetupColumn("##tag", ImGuiTableColumnFlags.WidthFixed, 32);

        foreach (var e in entries)
        {
            ImGui.TableNextRow();

            if (showCheckbox)
            {
                ImGui.TableNextColumn();
                var sel = e.SelectedForRemoval;
                if (ImGui.Checkbox($"###sel{e.ContentId}", ref sel))
                    e.SelectedForRemoval = sel;
            }

            ImGui.TableNextColumn();
            var isFav = config.IsFavorite(e.ContentId);
            ImGui.PushStyleColor(ImGuiCol.Text, FavStarColor);
            if (ImGui.Button((isFav ? "★" : "☆") + $"###star{e.ContentId}"))
                config.SetFavorite(e.ContentId, !isFav);
            ImGui.PopStyleColor();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(isFav ? "Unfavorite (removes the pin)" : "Favorite (pins to top)");

            ImGui.TableNextColumn();
            ImGui.TextColored(e.IsOnline ? OnlineColor : OfflineColor, "●");
            ImGui.SameLine();
            ImGui.TextUnformatted(e.Name);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.HomeWorldName);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.JobName);

            ImGui.TableNextColumn();
            if (e.IsOnline)
                ImGui.TextColored(OnlineColor, "now");
            else
                ImGui.TextDisabled(RelativeTime(e.LastSeenUnix));
            if (ImGui.IsItemHovered() && e.LastSeenUnix > 0)
                ImGui.SetTooltip("Last seen online (tracked by this plugin)");

            ImGui.TableNextColumn();
            if (ImGui.Button($"…###tag{e.ContentId}"))
                ImGui.OpenPopup($"###rowpop{e.ContentId}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Options: favorite, categories");
            if (ImGui.BeginPopup($"###rowpop{e.ContentId}"))
            {
                DrawRowPopup(e);
                ImGui.EndPopup();
            }
        }

        ImGui.EndTable();
    }

    private void DrawRowPopup(FriendEntry e)
    {
        var config = _plugin.Configuration;
        var isFav = config.IsFavorite(e.ContentId);

        if (_newCategoryFor == e.ContentId)
        {
            ImGui.Text("New category name:");
            ImGui.InputText("###newcatname", ref _newCategoryName, 64);
            if (ImGui.Button("Create"))
            {
                var name = string.IsNullOrWhiteSpace(_newCategoryName)
                    ? $"Category {config.Categories.Count + 1}"
                    : _newCategoryName.Trim();
                var cat = new FriendCategory { Name = name };
                config.Categories.Add(cat);
                MoveToCategory(e, cat);
                _newCategoryFor = 0;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                _newCategoryFor = 0;
            return;
        }

        if (ImGui.MenuItem(isFav ? "☆ Unfavorite" : "★ Favorite"))
            config.SetFavorite(e.ContentId, !isFav);

        ImGui.Separator();

        var currentCat = config.Categories.FirstOrDefault(c => c.MemberContentIds.Contains(e.ContentId));
        if (ImGui.BeginMenu("Move to category"))
        {
            if (config.Categories.Count == 0)
                ImGui.MenuItem("(no categories yet)", "", false, false);
            foreach (var cat in config.Categories)
            {
                var selected = cat == currentCat;
                if (ImGui.MenuItem(cat.Name, "", selected))
                    MoveToCategory(e, selected ? null : cat);
            }
            ImGui.EndMenu();
        }
        if (currentCat != null && ImGui.MenuItem("Remove from category"))
            MoveToCategory(e, null);

        ImGui.Separator();

        if (ImGui.MenuItem("New category..."))
        {
            _newCategoryFor = e.ContentId;
            _newCategoryName = string.Empty;
        }
    }

    private void MoveToCategory(FriendEntry e, FriendCategory? cat)
    {
        var config = _plugin.Configuration;
        // Favorites live in the pinned section; moving someone to a category unpins them.
        if (cat != null && config.IsFavorite(e.ContentId))
            config.SetFavorite(e.ContentId, false);
        foreach (var c in config.Categories)
            c.MemberContentIds.Remove(e.ContentId);
        cat?.MemberContentIds.Add(e.ContentId);
        config.Save();
    }

    private static bool MatchesSearch(FriendEntry e, string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;
        return e.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               e.HomeWorldName.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static string RelativeTime(long unixSeconds)
    {
        if (unixSeconds <= 0)
            return "never";
        var span = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if (span < TimeSpan.FromMinutes(1))
            return "just now";
        if (span < TimeSpan.FromHours(1))
            return $"{(int)span.TotalMinutes}m ago";
        if (span < TimeSpan.FromDays(1))
            return $"{(int)span.TotalHours}h ago";
        if (span < TimeSpan.FromDays(30))
            return $"{(int)span.TotalDays}d ago";
        if (span < TimeSpan.FromDays(365))
            return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
