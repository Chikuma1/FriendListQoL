using System;
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

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        FriendCache = new FriendCache(Configuration, log, framework, gameInteropProvider, dataManager);
        FriendRemover = new FriendRemover(log, framework, gameGui, addonLifecycle, Configuration);
        FriendRemover.OnSimulatedRemove = id => FriendCache.RemoveTestFriend(id);
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
        else
        {
            MainWindow.Toggle();
        }
    }

    private void OnLogin() => FriendCache.RequestRefresh();
}
