using AgentCommander.Cli.Tui;
using AgentCommander.Core;

if (args.Length > 0 && args[0] is "version" or "-v" or "--version")
{
    Console.WriteLine(AppInfo.Version);
    return 0;
}

if (args.Length > 0 && args[0] is "greet")
{
    Console.WriteLine($"Hello from the terminal, {(args.Length > 1 ? args[1] : "world")}!");
    return 0;
}

var app = new TuiApp();
await app.RunAsync();

return 0;
