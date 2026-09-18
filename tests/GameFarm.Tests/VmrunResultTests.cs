using GameFarm.VMware;

namespace GameFarm.Tests;

/// <summary>Confirmed live: vmrun does not consistently write its error messages to stderr --
/// "Cannot open VM: ..., A password is required for this operation" and "Cannot read the virtual
/// machine configuration file" both land on stdout. <see cref="VmrunResult.ErrorMessage"/> exists
/// specifically so callers never silently show a blank error for those.</summary>
public class VmrunResultTests
{
    [Fact]
    public void ErrorMessage_PrefersStandardErrorWhenPresent()
    {
        var result = new VmrunResult { StandardError = "real stderr message", StandardOutput = "some stdout noise" };
        Assert.Equal("real stderr message", result.ErrorMessage);
    }

    [Fact]
    public void ErrorMessage_FallsBackToStandardOutputWhenStandardErrorEmpty()
    {
        var result = new VmrunResult { StandardError = "", StandardOutput = "Error: Cannot read the virtual machine configuration file" };
        Assert.Equal("Error: Cannot read the virtual machine configuration file", result.ErrorMessage);
    }

    [Fact]
    public void ErrorMessage_FallsBackToStandardOutputWhenStandardErrorWhitespaceOnly()
    {
        var result = new VmrunResult { StandardError = "   \n", StandardOutput = "Error: A password is required for this operation" };
        Assert.Equal("Error: A password is required for this operation", result.ErrorMessage);
    }

    [Fact]
    public void ErrorMessage_TrimsWhitespace()
    {
        var result = new VmrunResult { StandardError = "  padded error  \n" };
        Assert.Equal("padded error", result.ErrorMessage);
    }

    [Fact]
    public void ErrorMessage_EmptyWhenBothEmpty()
    {
        var result = new VmrunResult();
        Assert.Equal("", result.ErrorMessage);
    }
}
