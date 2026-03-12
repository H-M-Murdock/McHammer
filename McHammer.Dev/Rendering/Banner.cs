using Spectre.Console;

namespace McHammer.Dev.Rendering;

public static class Banner
{
    public static void Render()
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("McHammer")
            .Centered()
            .Color(Color.Cyan1));

        AnsiConsole.Write(
            new Panel(
                    "[bold cyan]Monitoring Compliance Health Automation Management, Maintenance, Enforcement & Reporting[/]\n" +
                    "[grey]── Dev Console ──[/]")
                .Border(BoxBorder.Rounded)
                .BorderStyle(Style.Parse("cyan"))
                .Padding(2, 0)
                .Expand());

        AnsiConsole.WriteLine();
    }
}