namespace McHammer.Dev.Commands;

public interface IDevCommand
{
    string      Name        { get; }
    string      Description { get; }
    string      Category    { get; }
    Task        ExecuteAsync(CancellationToken ct = default);
}