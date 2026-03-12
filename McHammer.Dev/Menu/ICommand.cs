namespace McHammer.Dev.Menu;

public interface ICommand
{
    string Name        { get; }
    string Description { get; }
    Task ExecuteAsync(CancellationToken ct = default);
}