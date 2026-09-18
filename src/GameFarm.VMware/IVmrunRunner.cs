namespace GameFarm.VMware;

public sealed class VmrunResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public bool Success => ExitCode == 0;

    /// <summary>The actual error text to show/log for a failed call. vmrun does not consistently
    /// write its error messages to stderr -- confirmed directly: "Cannot open VM: ..., A password
    /// is required for this operation" and "Cannot read the virtual machine configuration file"
    /// both land on STDOUT, leaving <see cref="StandardError"/> empty and every caller that only
    /// read that field silently reporting a blank error message. Prefer <see cref="StandardError"/>
    /// when it has content (some failures do use it), otherwise fall back to
    /// <see cref="StandardOutput"/> rather than showing nothing.</summary>
    public string ErrorMessage => string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
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
