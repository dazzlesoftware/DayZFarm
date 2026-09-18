using GameFarm.Core.Plugins;

namespace GameFarm.Controller.Api;

/// <summary>
/// Host-side plugins: admin-authored command definitions (see docs/PLUGINS.md) that run
/// directly on this Controller's own host, with whatever privileges the Controller process
/// already has. Only a plugin's NAME is ever accepted here -- nothing about what it actually
/// runs comes from an HTTP request; that's fixed by a JSON file under
/// GameFarmOptions.PluginsDirectory, placed there by whoever has filesystem access to this
/// host. See docs/PLUGINS.md for the full safety model before adding any plugin definitions.
/// </summary>
public static class PluginsApi
{
    public static void MapPluginsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/plugins").WithTags("Plugins");

        api.MapGet("", (PluginService plugins) => Results.Ok(plugins.ListPlugins()));

        api.MapPost("/{name}/run", async (string name, PluginService plugins, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await plugins.RunAsync(name, ct));
            }
            catch (InvalidOperationException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }
}
