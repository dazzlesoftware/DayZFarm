using GameFarm.Controller.Api;
using GameFarm.Controller.Data;
using GameFarm.Controller.Hubs;
using GameFarm.Controller.Services;
using GameFarm.Core;
using GameFarm.Core.Configuration;
using GameFarm.Core.Interfaces;
using GameFarm.Core.Models;
using GameFarm.Core.Plugins;
using GameFarm.HyperV;
using GameFarm.VMware;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "Game Farm Controller");

builder.Services.AddOptions<GameFarmOptions>()
    .Bind(builder.Configuration.GetSection(GameFarmOptions.SectionName));

// Fail fast on invalid configuration rather than starting half-broken.
var configuredOptions = builder.Configuration.GetSection(GameFarmOptions.SectionName).Get<GameFarmOptions>() ?? new GameFarmOptions();
GameFarmOptionsValidator.ValidateAndThrow(configuredOptions);

// Structured, rolling-file logging under D:\GameFarm\Logs\Controller\. Never logs secrets —
// agent tokens and server passwords never pass through ILogger call sites in this codebase.
Directory.CreateDirectory(configuredOptions.ControllerLogsDirectory);
builder.Host.UseSerilog((_, loggerConfig) => loggerConfig
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(configuredOptions.ControllerLogsDirectory, "controller-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

// Without this, System.Text.Json serializes enums (VirtualMachineStatus, ConnectionStatus) as
// their raw numeric value -- the dashboard's JS looks up badge text/color by the string name
// ("Running", "Connected", ...), so every such lookup silently failed and rendered the bare
// number instead. See docs/TROUBLESHOOTING.md.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

// Exactly one hypervisor backend is registered, picked by GameFarmOptions.Hypervisor -- the two
// are mutually exclusive in practice on a single Windows host (enabling Hyper-V puts Windows
// itself in a hypervisor role that VMware Workstation then has to run nested under, with real
// performance/feature loss), so this never tries to run both providers at once. See
// docs/VMWARE-SETUP.md.
if (configuredOptions.Hypervisor == HypervisorType.VMwareWorkstation)
{
    builder.Services.AddSingleton<IVmrunRunner>(sp =>
        new ProcessVmrunRunner(configuredOptions.VmrunPath, sp.GetRequiredService<ILogger<ProcessVmrunRunner>>()));
    builder.Services.AddSingleton<IVirtualMachineProvider, VMwareVirtualMachineProvider>();
    // GPU-P (ManualGpuVirtualizationProvider) is Hyper-V-specific (Get-VMGpuPartitionAdapter) and
    // was explicitly scoped out of the VMware Workstation pass -- see docs/VMWARE-SETUP.md. This
    // stand-in keeps the DI graph valid (nothing here needs IPowerShellRunner, which also isn't
    // registered in this branch) rather than crashing every status poll.
    builder.Services.AddSingleton<IGpuVirtualizationProvider, UnsupportedGpuVirtualizationProvider>();
}
else
{
    builder.Services.AddSingleton<IPowerShellRunner, ProcessPowerShellRunner>();
    builder.Services.AddSingleton<IVirtualMachineProvider, HyperVVirtualMachineProvider>();
    builder.Services.AddSingleton<IGpuVirtualizationProvider, ManualGpuVirtualizationProvider>();
}
builder.Services.AddSingleton<IClientRepository, ClientRepository>();
builder.Services.AddSingleton<ISecretStore, SecretStore>();
builder.Services.AddSingleton<AgentPackageStore>();
builder.Services.AddSingleton<ConcurrencyGates>();
builder.Services.AddSingleton<AgentEndpointResolver>();
builder.Services.AddSingleton<ClientActionLogger>();
// Host-side plugins (see docs/PLUGINS.md) -- admin-authored command definitions run directly on
// this Controller's own host. Only ever triggered by name from the API/dashboard; what a plugin
// actually runs is fixed by a JSON file under PluginsDirectory, never anything from an HTTP
// request.
builder.Services.AddSingleton(sp =>
    new PluginService(configuredOptions.PluginsDirectory, sp.GetRequiredService<ILogger<PluginService>>()));
// The Agent's own DayZ-launch polling loop blocks its HTTP response for up to ~30s waiting for
// the DayZ_x64 process to appear (see DayZGameLauncher.LaunchAsync) -- 15s was shorter than
// that and caused a real, reproducible TaskCanceledException ("Agent unreachable... 15 seconds
// elapsing") on Start DayZ/Join whenever the launch legitimately took close to or over 15s. See
// docs/TROUBLESHOOTING.md.
builder.Services.AddHttpClient<IAgentClient, AgentHttpClient>(c => c.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<ClientOrchestrator>();
builder.Services.AddHostedService<StatusPollingService>();

// Default (~28.6MB) is too small for uploading an Agent update package (see
// docs/AGENT-UPDATES.md) -- a framework-dependent publish output is usually well under this, but
// leave real headroom rather than have a slightly larger build silently rejected. The multipart
// form parser has its own separate 128MB default limit independent of Kestrel's -- both need
// raising, or a large-enough upload would still be rejected despite the Kestrel bump above.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 300 * 1024 * 1024);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 300 * 1024 * 1024);

// Without this, SignalR's own JSON protocol serializes enums (VirtualMachineStatus,
// ConnectionStatus) as raw numbers -- it has its own JsonSerializerOptions, entirely separate
// from ConfigureHttpJsonOptions above, so that earlier fix didn't cover it. Symptom: the
// dashboard shows correct text right after the initial page load's plain HTTP fetch, then a few
// seconds later a SignalR "ClientsUpdated" push silently reverts the VM State / Connection
// columns back to raw numbers. See docs/TROUBLESHOOTING.md.
builder.Services.AddSignalR().AddJsonProtocol(options =>
    options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();

// Ensure the on-disk directory layout exists (Images/Instances/Config/Logs/Backups) up front.
var farmOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GameFarmOptions>>().Value;
foreach (var dir in new[] { farmOptions.ImagesDirectory, farmOptions.InstancesDirectory, farmOptions.ConfigDirectory, farmOptions.ControllerLogsDirectory, farmOptions.ClientLogsDirectory, farmOptions.BackupsDirectory, farmOptions.PluginsDirectory, farmOptions.AgentPackageDirectory })
    Directory.CreateDirectory(dir);

app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapClientsApi();
app.MapPluginsApi();
app.MapAgentPackageApi();
app.MapHub<FarmStatusHub>("/hubs/farm-status");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
