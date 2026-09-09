using DayZFarm.Core.Configuration;
using DayZFarm.Core.Models;
using Microsoft.Extensions.Options;

namespace DayZFarm.Controller.Services;

/// <summary>Resolves a client's current agent base address from the VM's live guest IP.</summary>
public sealed class AgentEndpointResolver
{
    private readonly int _agentPort;

    public AgentEndpointResolver(IOptions<DayZFarmOptions> options)
    {
        _agentPort = options.Value.AgentPort;
    }

    public Uri? Resolve(VirtualMachineInfo? vm)
    {
        if (vm?.GuestIpAddress is null) return null;
        return new Uri($"http://{vm.GuestIpAddress}:{_agentPort}/");
    }
}
