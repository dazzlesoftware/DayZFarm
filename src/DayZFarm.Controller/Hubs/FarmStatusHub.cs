using Microsoft.AspNetCore.SignalR;

namespace DayZFarm.Controller.Hubs;

/// <summary>Push channel the dashboard subscribes to for live status updates (no polling needed client-side).</summary>
public sealed class FarmStatusHub : Hub
{
}
