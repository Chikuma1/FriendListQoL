using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FriendListQoL.Removal;
using FriendListQoL.Services;
using FriendListQoL.Windows;

namespace FriendListQoL;

public sealed class Plugin : IDalamudPlugin
{
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string CommandName = "/flqol";

    public Configuration Configuration { get; init; }
    public WindowSystem WindowSystem { get; } = new("FriendListQoL");
    public FriendCache FriendCache { get; }
    public FriendRemover FriendRemover { get; }
    private NativeFavoriteMenu NativeFavoriteMenu { get; }
    private CalibrationService CalibrationService { get; }

    /// <summary>
    /// Test mode: the friend list shows 60 fake friends and purges are
    /// simulated. Nothing touches the real friend list. Not persisted —
    /// always off when the plugin loads.
    /// </summary>
    public bool TestMode
    {
        get => _testMode;
        set
        {
            _testMode = value;
            FriendCache.SetTestMode(value);
            FriendRemover.SimulateRemovals = value;
        }
    }
    private bool _testMode;

    private MainWindow MainWindow { get; }
    private ConfigWindow ConfigWindow { get; }

    private readonly ICommandManager _commands;
    private readonly IClientState _clientState;
    private readonly IChatGui _chatGui;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IFramework framework,
        IGameInteropProvider gameInteropProvider,
        IGameGui gameGui,
        IAddonLifecycle addonLifecycle,
        IContextMenu contextMenu,
        IDataManager dataManager,
        IChatGui chatGui,
        IPluginLog log)
    {
        PluginInterface = pluginInterface;
        _commands = commandManager;
        _clientState = clientState;
        _chatGui = chatGui;

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        FriendCache = new FriendCache(Configuration, log, framework, gameInteropProvider, dataManager);
        FriendRemover = new FriendRemover(log, framework, gameGui, commandManager, addonLifecycle, Configuration);
        FriendRemover.OnSimulatedRemove = id => FriendCache.RemoveTestFriend(id);
        Removal.FullAutoV2.ClickOffsetX = Configuration.ClickOffsetX;
        Removal.FullAutoV2.ClickOffsetY = Configuration.ClickOffsetY;
        NativeFavoriteMenu = new NativeFavoriteMenu(contextMenu, Configuration);
        CalibrationService = new CalibrationService(log, addonLifecycle, contextMenu, chatGui, gameGui, PluginInterface.GetPluginConfigDirectory());

        MainWindow = new MainWindow(this);
        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(ConfigWindow);

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += MainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += ConfigWindow.Toggle;

        _commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the FriendListQoL window. Add 'test' for test mode with fake friends, 'config' for settings."
        });

        _clientState.Login += OnLogin;
        if (_clientState.IsLoggedIn)
            FriendCache.RequestRefresh();
    }

    public void Dispose()
    {
        _clientState.Login -= OnLogin;
        _commands.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= MainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= ConfigWindow.Toggle;
        WindowSystem.RemoveAllWindows();
        NativeFavoriteMenu.Dispose();
        CalibrationService.Dispose();
        FriendRemover.Dispose();
        FriendCache.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();
        if (arg.Equals("config", StringComparison.OrdinalIgnoreCase))
        {
            ConfigWindow.Toggle();
        }
        else if (arg.Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            TestMode = !TestMode;
            MainWindow.IsOpen = true;
        }
        else if (arg.Equals("calibrate", StringComparison.OrdinalIgnoreCase))
        {
            CalibrationService.SetActive(!CalibrationService.IsActive);
        }
        else if (arg.Equals("fullauto", StringComparison.OrdinalIgnoreCase))
        {
            FriendRemover.AttemptFullAutomation = !FriendRemover.AttemptFullAutomation;
            var status = FriendRemover.AttemptFullAutomation ? "ON (experimental v2)" : "OFF (guided mode)";
            _chatGui.Print($"[FLQoL] Full automation attempt: {status}");
        }
        else if (arg.StartsWith("synctest ", StringComparison.OrdinalIgnoreCase))
        {
            var name = arg.Substring("synctest ".Length).Trim();
            var friend = FriendCache.Friends.FirstOrDefault(f =>
                f.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (friend == null)
            {
                _chatGui.Print($"[FLQoL] No friend matching '{name}' found.");
            }
            else
            {
                FriendRemover.SyncTestReport = msg => _chatGui.Print($"[FLQoL synctest] {msg}");
                _chatGui.Print($"[FLQoL] Synctest targeting '{friend.Name}' — safe, nothing will be removed.");
                FriendRemover.BeginSyncTest(friend);
            }
        }
        else if (arg.Equals("dumpmenu", StringComparison.OrdinalIgnoreCase))
        {
            var report = new Action<string>(msg => _chatGui.Print($"[FLQoL dumpmenu] {msg}"));
            FriendListQoL.Removal.FullAutoV2.SyncTestReport = report;
            FriendListQoL.Removal.FullAutoV2.DumpContextMenuStructure();
        }
        else if (arg.Equals("watchmenu", StringComparison.OrdinalIgnoreCase))
        {
            var report = new Action<string>(msg => _chatGui.Print($"[FLQoL watchmenu] {msg}"));
            FriendListQoL.Removal.FullAutoV2.SyncTestReport = report;
            FriendListQoL.Removal.FullAutoV2.WatchMenuIndices();
        }
        else if (arg.Equals("stopwatch", StringComparison.OrdinalIgnoreCase))
        {
            FriendListQoL.Removal.FullAutoV2.StopWatch();
        }
        else if (arg.Equals("capclick", StringComparison.OrdinalIgnoreCase))
        {
            FriendRemover.SyncTestReport = msg => _chatGui.Print($"[FLQoL synctest] {msg}");
            FriendRemover.BeginClickCapture();
        }
        else if (arg.Equals("calreset", StringComparison.OrdinalIgnoreCase))
        {
            FriendRemover.SyncTestReport = msg => _chatGui.Print($"[FLQoL synctest] {msg}");
            FriendRemover.ResetClickOffset();
        }
        else if (arg.StartsWith("setselect ", StringComparison.OrdinalIgnoreCase))
        {
            var name = arg.Substring("setselect ".Length).Trim();
            TestSetSelection(name);
        }
        else
        {
            MainWindow.Toggle();
        }
    }

    private void OnLogin() => FriendCache.RequestRefresh();

    /// <summary>
    /// Test command: sets AgentFriendlist.SelectedContentId to the given friend's
    /// content ID without starting a purge. Used to verify if the programmatic
    /// selection overrides a manual right-click.
    /// </summary>
    private unsafe void TestSetSelection(string name)
    {
        var friend = FriendCache.Friends.FirstOrDefault(f => 
            f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (friend == null)
        {
            _chatGui.Print($"[FLQoL] Friend '{name}' not found.");
            return;
        }

        try
        {
            var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
            if (agentModule == null)
            {
                _chatGui.Print("[FLQoL] AgentModule not available.");
                return;
            }

            var agent = agentModule->GetAgentByInternalId(
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Friendlist);
            if (agent == null || agent == (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface*)nint.Zero)
            {
                _chatGui.Print("[FLQoL] AgentFriendlist not available.");
                return;
            }

            var friendAgent = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentFriendlist*)agent;
            friendAgent->SelectedContentId = friend.ContentId;
            _chatGui.Print($"[FLQoL] Set selection to {friend.Name} (contentId={friend.ContentId}). Now right-click a DIFFERENT friend → Remove, and check whose name is in the dialog. Click No.");
        }
        catch (Exception ex)
        {
            _chatGui.Print($"[FLQoL] Failed: {ex.Message}");
        }
    }
}
