using McHammer.Dev.Commands;
using McHammer.Dev.Rendering;
using Spectre.Console;

namespace McHammer.Dev;

public class App
{
    private readonly List<IDevCommand> _commands = [];
    private readonly MenuRenderer      _renderer;

    public App()
    {
        _renderer = new MenuRenderer(_commands);
    }

    public void Register(IDevCommand command) => _commands.Add(command);

    public async Task RunAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            Banner.Render();

            IDevCommand? selected;
            try
            {
                selected = _renderer.Prompt();
            }
            catch (Exception)
            {
                // Ctrl+C im Prompt
                break;
            }

            if (selected is null) break;

            AnsiConsole.Clear();
            await selected.ExecuteAsync(ct);
        }

        AnsiConsole.MarkupLine("\n[cyan]McHammer Dev Console beendet.[/]\n");
    }
}