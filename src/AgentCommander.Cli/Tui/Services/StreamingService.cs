using Spectre.Console;

namespace AgentCommander.Cli.Tui.Services;

public class StreamingService
{
    public async Task StreamTextAsync(string text, int delayMs = 20, CancellationToken ct = default)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("思考中...", async ctx =>
            {
                ctx.Refresh();
                await Task.Delay(500, ct);

                ctx.Status("回复中...");
                ctx.Spinner(Spinner.Known.Arrow);

                foreach (var ch in text)
                {
                    ct.ThrowIfCancellationRequested();
                    Console.Write(ch);
                    await Task.Delay(delayMs, ct);
                }

                Console.WriteLine();
            });
    }
}
