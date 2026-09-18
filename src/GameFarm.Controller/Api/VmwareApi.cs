using GameFarm.Core.Interfaces;
using GameFarm.VMware;

namespace GameFarm.Controller.Api;

/// <summary>
/// VMware Workstation-specific settings that don't fit the per-client shape of ClientsApi.
/// Currently just the master VMX's encryption password (see docs/VMWARE-SETUP.md's "Encrypted
/// master VM" section) -- a single farm-wide secret, stored via the same DPAPI-backed
/// <see cref="ISecretStore"/> as agent tokens/server passwords, never returned once set.
/// </summary>
public static class VmwareApi
{
    public static void MapVmwareApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/vmware").WithTags("VMware");

        api.MapGet("/master-password", (ISecretStore secrets) =>
            Results.Ok(new { set = secrets.Get(VmwareSecretNames.MasterVmxPassword) is not null }));

        api.MapPost("/master-password", (SetVmwareMasterPasswordRequest req, ISecretStore secrets) =>
        {
            if (string.IsNullOrEmpty(req.Password))
            {
                secrets.Delete(VmwareSecretNames.MasterVmxPassword);
                return Results.Ok(new { set = false });
            }
            secrets.Set(VmwareSecretNames.MasterVmxPassword, req.Password);
            return Results.Ok(new { set = true });
        });
    }
}

public sealed record SetVmwareMasterPasswordRequest(string? Password);
