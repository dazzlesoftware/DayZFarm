using GameFarm.Core.Configuration;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Services;

/// <summary>
/// Appends a per-client, human-readable action log under
/// <c>D:\GameFarm\Logs\Clients\&lt;name&gt;\actions-yyyyMMdd.log</c> — VM actions, agent commands,
/// join/update attempts, and errors. Complements the structured Serilog controller log (which
/// captures everything in one place); this one makes "what happened to DayZ-014 this week"
/// answerable by looking at a single small file. Never writes secrets (tokens/passwords) —
/// callers only ever pass action names and short result summaries.
/// </summary>
public sealed class ClientActionLogger
{
    private readonly string _clientLogsRoot;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public ClientActionLogger(IOptions<GameFarmOptions> options)
    {
        _clientLogsRoot = options.Value.ClientLogsDirectory;
    }

    public async Task LogAsync(string clientName, string category, string message, bool isError = false)
    {
        var dir = Path.Combine(_clientLogsRoot, clientName);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"actions-{DateTime.UtcNow:yyyyMMdd}.log");
        var line = $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [{(isError ? "ERROR" : "INFO")}] [{category}] {message}{Environment.NewLine}";

        await WriteLock.WaitAsync();
        try
        {
            await File.AppendAllTextAsync(file, line);
        }
        finally
        {
            WriteLock.Release();
        }
    }
}
