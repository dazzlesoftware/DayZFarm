using GameFarm.Core.Configuration;
using GameFarm.Core.Models;
using Microsoft.Extensions.Options;

namespace GameFarm.Controller.Services;

/// <summary>Resolves a client's current agent base address from the VM's live guest IP.</summary>
public sealed class AgentEndpointResolver
{
    private readonly int _agentPort;

    public AgentEndpointResolver(IOptions<GameFarmOptions> options)
    {
        _agentPort = options.Value.AgentPort;
    }

    public Uri? Resolve(VirtualMachineInfo? vm)
    {
        if (vm?.GuestIpAddress is null) return null;
        return new Uri($"http://{vm.GuestIpAddress}:{_agentPort}/");
    }
}
