using GameFarm.Core.Interfaces;
using GameFarm.Core.Models;

namespace GameFarm.Agent.Games.DayZ;

/// <summary>DayZ's own Steam App ID -- lives in this game module rather than anywhere central,
/// since a different game module would have a different one entirely.</summary>
public static class DayZSteamApp
{
    public const uint Id = 221100;
}

/// <summary>DayZ's <see cref="IGameProfile"/> -- registered separately from
/// <see cref="DayZGameLauncher"/> itself so <see cref="IWorkshopManager"/> can depend on "which
/// game is this" without depending on the launcher (which in turn depends on
/// <see cref="IWorkshopManager"/> to resolve mod paths before launch -- a real DI cycle if both
/// pointed at each other).</summary>
public sealed class DayZGameProfile : IGameProfile
{
    public uint SteamAppId => DayZSteamApp.Id;
}

/// <summary>
/// Builds DayZ's own command-line arguments. Kept separate from <see cref="DayZGameLauncher"/>'s
/// process-launch code so new arguments (e.g. -mod, -noPause, -adminLog) can be added without
/// touching launch logic. This is DayZ-specific by design -- a different game module builds its
/// own argument list from the same <see cref="GameLaunchOptions"/> shape however that game's
/// command line actually works.
/// </summary>
public static class DayZLaunchArgumentBuilder
{
    public static IReadOnlyList<string> Build(GameLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ServerAddress))
            throw new ArgumentException("ServerAddress is required.", nameof(options));
        if (options.ServerPort is < 1 or > 65535)
            throw new ArgumentException("ServerPort must be between 1 and 65535.", nameof(options));

        var args = new List<string>
        {
            $"-connect={options.ServerAddress}",
            $"-port={options.ServerPort}"
        };

        if (!string.IsNullOrWhiteSpace(options.ServerPassword))
            args.Add($"-password={options.ServerPassword}");

        if (options.Mods.Count > 0)
            args.Add($"-mod={string.Join(";", options.Mods)}");

        // Reasonable low-graphics/automation defaults; can be overridden via AdditionalArguments.
        args.Add("-noSplash");
        args.Add("-skipIntro");
        args.Add("-nolauncher");

        args.AddRange(options.AdditionalArguments);

        return args;
    }

    public static string BuildArgumentString(GameLaunchOptions options) =>
        string.Join(' ', Build(options).Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
