using System.ComponentModel.DataAnnotations;

namespace GameFarm.Core.Models;

/// <summary>Persistent record for one managed DayZ client (one VM, one Steam account).</summary>
public sealed class GameClientInstance
{
    public int Id { get; set; }

    [Required, MaxLength(64)]
    public required string Name { get; set; }

    [Required, MaxLength(64)]
    public required string VmName { get; set; }

    [MaxLength(64)]
    public string? SteamAccountName { get; set; }

    public ulong? SteamId64 { get; set; }

    [Required, MaxLength(255)]
    public required string ServerAddress { get; set; }

    [Range(1, 65535)]
    public int ServerPort { get; set; } = 2302;

    /// <summary>Name of a secret in the secret store (DPAPI/Credential Manager) — never the password itself.</summary>
    [MaxLength(128)]
    public string? ServerPasswordSecretName { get; set; }

    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public bool AutoUpdate { get; set; } = true;

    public List<string> RequiredMods { get; set; } = new();

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Validates a <see cref="GameClientInstance"/> beyond attribute validation (cross-field rules).</summary>
public static class GameClientInstanceValidator
{
    public static IReadOnlyList<string> Validate(GameClientInstance instance)
    {
        var errors = new List<string>();

        var ctx = new ValidationContext(instance);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(instance, ctx, results, validateAllProperties: true))
        {
            errors.AddRange(results.Select(r => r.ErrorMessage ?? "Invalid field."));
        }

        if (string.IsNullOrWhiteSpace(instance.Name))
            errors.Add("Name is required.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(instance.VmName, "^[A-Za-z0-9_-]+$"))
            errors.Add("VmName must contain only letters, digits, hyphens and underscores.");

        if (instance.ServerPort is < 1 or > 65535)
            errors.Add("ServerPort must be between 1 and 65535.");

        return errors;
    }
}
