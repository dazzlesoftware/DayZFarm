using System.ComponentModel.DataAnnotations;

namespace DayZFarm.Core.Configuration;

/// <summary>Root configuration section ("DayZFarm" in appsettings.json). Validated at startup.</summary>
public sealed class DayZFarmOptions
{
    public const string SectionName = "DayZFarm";

    [Required]
    public string RootDirectory { get; set; } = @"D:\DayZFarm";

    [Required]
    public string MasterVhdx { get; set; } = @"D:\DayZFarm\Images\DayZ-Master.vhdx";

    [Required]
    public string VirtualSwitch { get; set; } = "DayZFarmSwitch";

    [Range(1, 64)]
    public int DefaultCpuCount { get; set; } = 4;

    [Range(1, 256)]
    public int DefaultMemoryGB { get; set; } = 6;

    [Range(1, 3600)]
    public int StatusPollSeconds { get; set; } = 10;

    [Range(1, 200)]
    public int MaxConcurrentVmOperations { get; set; } = 10;

    [Range(1, 500)]
    public int MaxConcurrentAgentCommands { get; set; } = 20;

    [Range(1, 500)]
    public int UpdateBatchSize { get; set; } = 5;

    /// <summary>TCP port the guest agent listens on inside every VM.</summary>
    [Range(1, 65535)]
    public int AgentPort { get; set; } = 5099;

    /// <summary>Default reconnect backoff schedule in seconds, applied in order then held at the last value.</summary>
    public int[] ReconnectBackoffSeconds { get; set; } = { 5, 10, 30, 60 };

    [Range(1, 86400)]
    public int MaxReconnectBackoffSeconds { get; set; } = 300;

    public string InstancesDirectory => Path.Combine(RootDirectory, "Instances");
    public string ImagesDirectory => Path.Combine(RootDirectory, "Images");
    public string ConfigDirectory => Path.Combine(RootDirectory, "Config");
    public string LogsDirectory => Path.Combine(RootDirectory, "Logs");
    public string BackupsDirectory => Path.Combine(RootDirectory, "Backups");
    public string ControllerLogsDirectory => Path.Combine(LogsDirectory, "Controller");
    public string ClientLogsDirectory => Path.Combine(LogsDirectory, "Clients");
    public string DatabasePath => Path.Combine(ConfigDirectory, "dayzfarm.db");

    /// <summary>
    /// Conventional path for the shared Steam Library disk (see scripts/Create-Master.ps1 /
    /// docs/MASTER-IMAGE.md). This is a real disk installed into directly — never copied — so
    /// this path either exists (because Create-Master.ps1 built it, or it was built manually via
    /// scripts/Create-SteamLibraryDisk.ps1) or it doesn't; <see cref="ClientOrchestrator"/> auto-
    /// detects its presence and gives dashboard-created clients a differencing disk directly
    /// against it when found, exactly like scripts/Create-Client.ps1 does.
    /// </summary>
    public string SteamLibraryVhdx => Path.Combine(ImagesDirectory, "SteamLibrary-Master.vhdx");
}

/// <summary>Fails fast with a clear message rather than letting the app start half-configured.</summary>
public static class DayZFarmOptionsValidator
{
    public static void ValidateAndThrow(DayZFarmOptions options)
    {
        var ctx = new ValidationContext(options);
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, ctx, results, validateAllProperties: true))
        {
            var msg = string.Join("; ", results.Select(r => r.ErrorMessage));
            throw new InvalidOperationException($"Invalid DayZFarm configuration: {msg}");
        }

        if (options.ReconnectBackoffSeconds is null || options.ReconnectBackoffSeconds.Length == 0)
            throw new InvalidOperationException("ReconnectBackoffSeconds must contain at least one value.");

        if (options.ReconnectBackoffSeconds.Any(s => s <= 0))
            throw new InvalidOperationException("ReconnectBackoffSeconds values must be positive.");
    }
}
