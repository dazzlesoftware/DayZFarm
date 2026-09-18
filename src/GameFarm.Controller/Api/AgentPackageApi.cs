using GameFarm.Controller.Services;

namespace GameFarm.Controller.Api;

/// <summary>
/// Upload/inspect the Agent update package that gets pushed out to guest agents (see
/// docs/AGENT-UPDATES.md). Actually pushing it to a client lives on ClientsApi
/// (<c>POST /api/clients/{id}/agent/update-package</c>) since that needs a client/agent context;
/// this group only manages the one stored package itself.
/// </summary>
public static class AgentPackageApi
{
    public static void MapAgentPackageApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/agent-package").WithTags("AgentPackage");

        api.MapGet("", (AgentPackageStore store) =>
        {
            var info = store.GetInfo();
            return Results.Ok(info is null
                ? new { uploaded = false }
                : new { uploaded = true, info.VersionHash, info.OriginalFileName, info.SizeBytes, info.UploadedUtc });
        });

        api.MapPost("", async (HttpRequest request, AgentPackageStore store) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart/form-data with a 'file' field." });

            var form = await request.ReadFormAsync();
            var file = form.Files["file"];
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded." });
            if (!string.Equals(Path.GetExtension(file.FileName), ".zip", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Expected a .zip file (dotnet publish output, zipped). See docs/AGENT-UPDATES.md." });

            await using var stream = file.OpenReadStream();
            var info = await store.SaveAsync(stream, file.FileName);
            return Results.Ok(new { info.VersionHash, info.OriginalFileName, info.SizeBytes, info.UploadedUtc });
        });
    }
}
