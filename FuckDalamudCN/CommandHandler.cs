using Dalamud.Game.Command;
using Dalamud.Plugin.Services;
using FuckDalamudCN.Windows;

namespace FuckDalamudCN;

internal sealed class CommandHandler : IDisposable
{
    private readonly ICommandManager _commandManager;
    private readonly ConfigWindow _configWindow;

    public CommandHandler(
        ICommandManager commandManager,
        ConfigWindow configWindow
    )
    {
        _commandManager = commandManager;
        _configWindow = configWindow;
        _commandManager.AddHandler("/fdcn", new CommandInfo(OnCommand)
        {
            HelpMessage = "打开配置窗口."
        });
    }

    public void Dispose()
    {
        _commandManager.RemoveHandler("/fdcn");
    }

    private void OnCommand(string command, string arguments)
    {
        _configWindow.Toggle();
    }
}