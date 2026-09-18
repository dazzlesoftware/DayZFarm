namespace GameFarm.Core.Models;

/// <summary>Generic launch parameters for joining a game server -- shared shape across whatever
/// game module (<see cref="GameFarm.Core.Interfaces.IGameLauncher"/> implementation) is active.
/// A concrete game's own argument-building logic (e.g. DayZ's <c>-connect=</c>/<c>-mod=</c>
/// syntax) lives in that game's own module, not here -- a different game could need an entirely
/// different command-line shape from the same options.</summary>
public sealed class GameLaunchOptions
{
    public required string ServerAddress { get; set; }
    public required int ServerPort { get; set; }
    public string? ServerPassword { get; set; }
    public List<string> Mods { get; set; } = new();
    public List<string> AdditionalArguments { get; set; } = new();
}
