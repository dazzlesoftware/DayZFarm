namespace GameFarm.VMware;

/// <summary>
/// Builds the final argument list passed to vmrun.exe, inserting the <c>-vp &lt;password&gt;</c>
/// authentication flag when a password is configured (see docs/VMWARE-SETUP.md's "Encrypted
/// master VM" section). vmrun requires every AUTHENTICATION-FLAG (<c>-T</c>, <c>-vp</c>, ...) to
/// appear before the command itself, so this can't just be appended -- it must be inserted right
/// after the existing <c>-T &lt;hostType&gt;</c> pair every caller already puts first. Kept as a
/// small, pure, independently testable function rather than inlined into
/// <see cref="ProcessVmrunRunner"/>, which otherwise has no unit tests (it's a thin
/// external-process wrapper, consistent with how GameFarm.HyperV's ProcessPowerShellRunner is
/// tested only indirectly).
/// </summary>
public static class VmrunArguments
{
    public static IReadOnlyList<string> WithPassword(IReadOnlyList<string> arguments, string? password)
    {
        if (string.IsNullOrEmpty(password)) return arguments;

        var result = new List<string>(arguments.Count + 2);
        var insertAt = arguments.Count >= 2 && arguments[0] == "-T" ? 2 : 0;
        result.AddRange(arguments.Take(insertAt));
        result.Add("-vp");
        result.Add(password);
        result.AddRange(arguments.Skip(insertAt));
        return result;
    }
}
