using DayZFarm.Controller.Services;
using DayZFarm.Core.Models;
using DayZFarm.Shared;

namespace DayZFarm.Controller.Api;

/// <summary>Documented REST surface for client and farm-wide operations. See docs/ARCHITECTURE.md.</summary>
public static class ClientsApi
{
    public static void MapClientsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/clients").WithTags("Clients");

        api.MapGet("", async (ClientOrchestrator orchestrator, CancellationToken ct) =>
            Results.Ok(await orchestrator.GetAllViewsAsync(ct)));

        api.MapGet("/{id:int}", async (int id, ClientOrchestrator orchestrator, CancellationToken ct) =>
        {
            var view = await orchestrator.GetViewAsync(id, ct);
            return view is null ? Results.NotFound() : Results.Ok(view);
        });

        api.MapPost("", async (CreateClientRequest req, ClientOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var instance = new DayZClientInstance
                {
                    Name = req.Name,
                    VmName = req.VmName,
                    ServerAddress = req.ServerAddress,
                    ServerPort = req.ServerPort,
                    SteamAccountName = req.SteamAccountName,
                    AutoStart = req.AutoStart,
                    AutoReconnect = req.AutoReconnect,
                    AutoUpdate = req.AutoUpdate,
                    RequiredMods = req.RequiredMods ?? new()
                };
                var id = await orchestrator.CreateClientAsync(instance, ct);
                return Results.Created($"/api/clients/{id}", new { id });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        api.MapPut("/{id:int}", async (int id, UpdateClientRequest req, ClientOrchestrator orchestrator, CancellationToken ct) =>
        {
            try
            {
                var updated = await orchestrator.UpdateClientAsync(id, req, ct);
                return updated ? Results.Ok() : Results.NotFound();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        api.MapDelete("/{id:int}", async (int id, ClientOrchestrator orchestrator, CancellationToken ct) =>
        {
            await orchestrator.DeleteClientAsync(id, ct);
            return Results.NoContent();
        });

        api.MapPost("/bulk-create", async (BulkCreateClientsRequest req, ClientOrchestrator orchestrator, CancellationToken ct) =>
            Results.Ok(await orchestrator.CreateClientsBulkAsync(req, ct)));

        api.MapPost("/{id:int}/agent/register-token", (int id, RegisterTokenRequest req, ClientOrchestrator orchestrator) =>
        {
            // The Name in RegisterTokenRequest must match the client's Name; resolved via a lookup in a real
            // implementation this endpoint would validate ownership. Kept simple for the MVP.
            orchestrator.RegisterAgentToken(req.ClientName, req.Token);
            return Results.Ok();
        });

        api.MapPost("/{id:int}/vm/start", (int id, ClientOrchestrator o) => VmActionByClientAsync(id, o, o.StartVmAsync));
        api.MapPost("/{id:int}/vm/stop", (int id, ClientOrchestrator o) => VmActionByClientAsync(id, o, o.StopVmAsync));
        api.MapPost("/{id:int}/vm/restart", (int id, ClientOrchestrator o) => VmActionByClientAsync(id, o, o.RestartVmAsync));
        api.MapPost("/{id:int}/vm/shutdown", (int id, ClientOrchestrator o) => VmActionByClientAsync(id, o, o.ShutdownVmAsync));

        api.MapPost("/{id:int}/steam/start", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.SteamStart, ct));
        api.MapPost("/{id:int}/steam/stop", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.SteamStop, ct));
        api.MapPost("/{id:int}/steam/restart", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.SteamRestart, ct));

        api.MapPost("/{id:int}/dayz/start", (int id, ClientOrchestrator o, CancellationToken ct) => o.JoinAsync(id, ct));
        api.MapPost("/{id:int}/dayz/stop", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.DayZStop, ct));
        api.MapPost("/{id:int}/dayz/restart", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.DayZRestart, ct));

        api.MapPost("/{id:int}/join", (int id, ClientOrchestrator o, CancellationToken ct) => o.JoinAsync(id, ct));
        api.MapPost("/{id:int}/disconnect", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.Disconnect, ct));
        api.MapPost("/{id:int}/update", (int id, ClientOrchestrator o, CancellationToken ct) => o.SendAgentCommandAsync(id, AgentApiRoutes.Update, ct));

        api.MapGet("/{id:int}/mods", async (int id, ClientOrchestrator o, CancellationToken ct) =>
        {
            var view = await o.GetViewAsync(id, ct);
            if (view is null) return Results.NotFound();
            var mods = await o.GetModsAsync(id, ct);
            return Results.Ok(new
            {
                RequiredMods = view.RequiredMods,
                Installed = mods?.Mods ?? new List<WorkshopModStatus>(),
                AgentReachable = mods is not null
            });
        });

        api.MapPost("/{id:int}/mods/ensure", (int id, ClientOrchestrator o, CancellationToken ct) => o.EnsureModsAsync(id, ct));

        api.MapGet("/{id:int}/logs", async (int id, ClientOrchestrator o, CancellationToken ct) =>
        {
            var view = await o.GetViewAsync(id, ct);
            if (view is null) return Results.NotFound();

            var agentLogs = await o.GetAgentLogsAsync(id, ct);
            var actionLog = await o.GetActionLogTailAsync(view.Name, ct: ct);

            // Give a specific reason instead of a one-size-fits-all "unreachable or no token" —
            // these are different problems with different fixes (see docs/TROUBLESHOOTING.md).
            var unavailableReason = agentLogs is not null ? null
                : view.VmStatus != DayZFarm.Core.Models.VirtualMachineStatus.Running
                    ? "VM is not running."
                : !view.TokenRegistered
                    ? "No agent token registered for this client yet — use the Register Token action."
                : view.GuestIpAddress is null
                    ? "VM is running but has no known guest IP yet — wait a few seconds for it to boot, then refresh."
                    : "Agent token is registered but the agent isn't responding — check the agent Windows Service and firewall inside the VM (see docs/TROUBLESHOOTING.md).";

            return Results.Ok(new
            {
                view.LastError,
                view.ConnectionStatus,
                DayZLog = agentLogs?.Lines ?? new List<string> { unavailableReason! },
                DayZLogSource = agentLogs?.SourceName,
                ActionLog = actionLog
            });
        });

        // ---- Bulk / global operations ----
        var bulk = app.MapGroup("/api/clients").WithTags("Bulk");

        bulk.MapPost("/start-selected", (int[] ids, ClientOrchestrator o, CancellationToken ct) => BulkVmAsync(ids, o, o.StartVmAsync, ct));
        bulk.MapPost("/stop-selected", (int[] ids, ClientOrchestrator o, CancellationToken ct) => BulkVmAsync(ids, o, o.StopVmAsync, ct));
        bulk.MapPost("/reboot-selected", (int[] ids, ClientOrchestrator o, CancellationToken ct) => BulkVmAsync(ids, o, o.RestartVmAsync, ct));
        bulk.MapPost("/join-selected", (int[] ids, ClientOrchestrator o, CancellationToken ct) => BulkAgentAsync(ids, o, ct, id => o.JoinAsync(id, ct)));
        bulk.MapPost("/restart-dayz-selected", (int[] ids, ClientOrchestrator o, CancellationToken ct) =>
            BulkAgentAsync(ids, o, ct, id => o.SendAgentCommandAsync(id, AgentApiRoutes.DayZRestart, ct)));
        bulk.MapPost("/update-selected", async (int[] ids, ClientOrchestrator o, CancellationToken ct) =>
            Results.Ok(await o.UpdateClientsAsync(ids, null, ct)));

        bulk.MapPost("/start-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return await BulkVmAsync(all.Select(v => v.Id).ToArray(), o, o.StartVmAsync, ct);
        });
        bulk.MapPost("/stop-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return await BulkVmAsync(all.Select(v => v.Id).ToArray(), o, o.StopVmAsync, ct);
        });
        bulk.MapPost("/join-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return await BulkAgentAsync(all.Select(v => v.Id).ToArray(), o, ct, id => o.JoinAsync(id, ct));
        });
        bulk.MapPost("/disconnect-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return await BulkAgentAsync(all.Select(v => v.Id).ToArray(), o, ct, id => o.SendAgentCommandAsync(id, AgentApiRoutes.Disconnect, ct));
        });
        bulk.MapPost("/restart-dayz-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return await BulkAgentAsync(all.Select(v => v.Id).ToArray(), o, ct, id => o.SendAgentCommandAsync(id, AgentApiRoutes.DayZRestart, ct));
        });
        bulk.MapPost("/update-all", async (ClientOrchestrator o, CancellationToken ct) =>
        {
            var all = await o.GetAllViewsAsync(ct);
            return Results.Ok(await o.UpdateClientsAsync(all.Select(v => v.Id), null, ct));
        });
    }

    private static async Task<IResult> VmActionByClientAsync(int id, ClientOrchestrator orchestrator, Func<string, CancellationToken, Task> action)
    {
        var view = await orchestrator.GetViewAsync(id, CancellationToken.None);
        if (view is null) return Results.NotFound();
        await action(view.VmName, CancellationToken.None);
        return Results.Ok(AgentCommandResult.Ok());
    }

    private static async Task<IResult> BulkVmAsync(int[] ids, ClientOrchestrator orchestrator, Func<string, CancellationToken, Task> action, CancellationToken ct)
    {
        var results = new List<object>();
        foreach (var id in ids)
        {
            var view = await orchestrator.GetViewAsync(id, ct);
            if (view is null) { results.Add(new { id, success = false, message = "not found" }); continue; }
            try
            {
                await action(view.VmName, ct);
                results.Add(new { id, success = true });
            }
            catch (Exception ex)
            {
                results.Add(new { id, success = false, message = ex.Message });
            }
        }
        return Results.Ok(results);
    }

    private static async Task<IResult> BulkAgentAsync(int[] ids, ClientOrchestrator orchestrator, CancellationToken ct, Func<int, Task<AgentCommandResult>> action)
    {
        var tasks = ids.Select(async id => new { id, result = await action(id) });
        var results = await Task.WhenAll(tasks);
        return Results.Ok(results);
    }
}

public sealed record CreateClientRequest(
    string Name,
    string VmName,
    string ServerAddress,
    int ServerPort,
    string? SteamAccountName,
    bool AutoStart,
    bool AutoReconnect,
    bool AutoUpdate,
    List<string>? RequiredMods);

public sealed record RegisterTokenRequest(string ClientName, string Token);
