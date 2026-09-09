namespace DayZFarm.HyperV;

public sealed class PowerShellResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Abstraction over "run a PowerShell script and get output back". Kept separate from
/// <see cref="HyperVVirtualMachineProvider"/> so unit tests can supply canned output without
/// needing Hyper-V, PowerShell, or administrator rights on the test machine.
/// </summary>
public interface IPowerShellRunner
{
    Task<PowerShellResult> RunAsync(string script, CancellationToken ct = default);
}
