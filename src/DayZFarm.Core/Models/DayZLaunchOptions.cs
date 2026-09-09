namespace DayZFarm.Core.Models;

/// <summary>The DayZ Steam App ID (used for both the client and, historically, the dedicated server tool).</summary>
public static class SteamAppIds
{
    public const uint DayZ = 221100;
}

public sealed class DayZLaunchOptions
{
    public required string ServerAddress { get; set; }
    public required int ServerPort { get; set; }
    public string? ServerPassword { get; set; }
    public List<string> Mods { get; set; } = new();
    public List<string> AdditionalArguments { get; set; } = new();
}

/// <summary>
/// Builds DayZ command-line arguments. Kept separate from any process-launch code so new
/// arguments (e.g. -mod, -noPause, -adminLog) can be added without touching launch logic.
/// </summary>
public static class DayZLaunchArgumentBuilder
{
    public static IReadOnlyList<string> Build(DayZLaunchOptions options)
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

    public static string BuildArgumentString(DayZLaunchOptions options) =>
        string.Join(' ', Build(options).Select(QuoteIfNeeded));

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
