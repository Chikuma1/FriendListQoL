using System;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Plugin.Services;

namespace FriendListQoL;

/// <summary>
/// Adds "★ Favorite" / "☆ Unfavorite" to the right-click menu of the game's
/// own Friend List window, so you can favorite people without opening the
/// plugin window.
/// </summary>
public sealed class NativeFavoriteMenu : IDisposable
{
    private readonly IContextMenu _contextMenu;
    private readonly Configuration _config;

    public NativeFavoriteMenu(IContextMenu contextMenu, Configuration config)
    {
        _contextMenu = contextMenu;
        _config = config;
        _contextMenu.OnMenuOpened += OnMenuOpened;
    }

    public void Dispose()
    {
        _contextMenu.OnMenuOpened -= OnMenuOpened;
    }

    private void OnMenuOpened(MenuOpenedArgs args)
    {
        // Only react to the game's Friend List window. (Verify in-game: this
        // is the addon name the game reports when its friend list rows are
        // right-clicked.)
        if (!string.Equals(args.AddonName, "FriendList", StringComparison.Ordinal))
            return;

        if (args.Target is not MenuTargetDefault target)
            return;

        var contentId = target.TargetContentId;
        if (contentId == 0)
            return;

        var isFav = _config.IsFavorite(contentId);
        args.AddMenuItem(new MenuItem
        {
            Name = isFav ? "☆ Unfavorite" : "★ Favorite",
            PrefixChar = 'F',
            OnClicked = _ => _config.SetFavorite(contentId, !isFav),
        });
    }
}
