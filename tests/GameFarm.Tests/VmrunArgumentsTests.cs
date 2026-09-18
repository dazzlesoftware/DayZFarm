using GameFarm.VMware;

namespace GameFarm.Tests;

public class VmrunArgumentsTests
{
    [Fact]
    public void WithPassword_NullPassword_ReturnsArgumentsUnchanged()
    {
        var args = new[] { "-T", "ws", "listSnapshots", "master.vmx" };
        var result = VmrunArguments.WithPassword(args, null);
        Assert.Same(args, result);
    }

    [Fact]
    public void WithPassword_EmptyPassword_ReturnsArgumentsUnchanged()
    {
        var args = new[] { "-T", "ws", "listSnapshots", "master.vmx" };
        var result = VmrunArguments.WithPassword(args, "");
        Assert.Same(args, result);
    }

    [Fact]
    public void WithPassword_InsertsRightAfterHostTypeFlag()
    {
        var args = new[] { "-T", "ws", "listSnapshots", "master.vmx" };
        var result = VmrunArguments.WithPassword(args, "s3cret");
        Assert.Equal(new[] { "-T", "ws", "-vp", "s3cret", "listSnapshots", "master.vmx" }, result);
    }

    [Fact]
    public void WithPassword_PreservesEveryOtherArgumentAndOrder()
    {
        var args = new[] { "-T", "ws", "clone", "master.vmx", "client.vmx", "linked", "-snapshot=Baseline", "-cloneName=Client-001" };
        var result = VmrunArguments.WithPassword(args, "s3cret");
        Assert.Equal(
            new[] { "-T", "ws", "-vp", "s3cret", "clone", "master.vmx", "client.vmx", "linked", "-snapshot=Baseline", "-cloneName=Client-001" },
            result);
    }

    [Fact]
    public void WithPassword_NoHostTypeFlag_PrependsPasswordFlagsAtStart()
    {
        // Defensive: every real caller in this codebase always starts with "-T ws", but this
        // shouldn't corrupt an argument list that (for whatever reason) doesn't.
        var args = new[] { "list" };
        var result = VmrunArguments.WithPassword(args, "s3cret");
        Assert.Equal(new[] { "-vp", "s3cret", "list" }, result);
    }

    [Fact]
    public void WithPassword_TooFewArgumentsForHostTypePair_PrependsAtStart()
    {
        var args = new[] { "-T" };
        var result = VmrunArguments.WithPassword(args, "s3cret");
        Assert.Equal(new[] { "-vp", "s3cret", "-T" }, result);
    }
}
