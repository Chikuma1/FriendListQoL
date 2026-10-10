using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FriendListQoL.Windows;

public class ConfigWindow : Window
{
    private readonly Plugin _plugin;

    public ConfigWindow(Plugin plugin)
        : base("FriendListQoL Settings###FriendListQoLConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _plugin = plugin;
        Size = new Vector2(420, 0);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var config = _plugin.Configuration;

        ImGui.TextWrapped("Favorites are stored by character ID, so they survive name changes and world transfers.");
        ImGui.Spacing();

        var delay = config.RemovalDelayMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Delay between removals (ms)", ref delay, 500, 10000))
        {
            config.RemovalDelayMs = delay;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Pause between each automated removal so the server isn't hammered.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextWrapped("Note: the purge feature drives the game's own remove-friend flow. " +
                          "The final trigger step needs one in-game calibration pass — see README \"Calibrating removal\".");
    }
}
