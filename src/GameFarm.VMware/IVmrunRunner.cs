namespace GameFarm.VMware;

public sealed class VmrunResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Abstraction over "run vmrun.exe with these arguments and get output back". Kept separate
/// from <see cref="VMwareVirtualMachineProvider"/> so unit tests can supply canned output
/// without needing VMware Workstation installed on the test machine -- mirrors
/// GameFarm.HyperV's IPowerShellRunner/ProcessPowerShellRunner split.
/// </summary>
public interface IVmrunRunner
{
    Task<VmrunResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default);
}
