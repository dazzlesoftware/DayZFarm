using DayZFarm.Controller.Api;
using DayZFarm.Controller.Data;
using DayZFarm.Controller.Hubs;
using DayZFarm.Controller.Services;
using DayZFarm.Core;
using DayZFarm.Core.Configuration;
using DayZFarm.Core.Interfaces;
using DayZFarm.Core.Models;
using DayZFarm.HyperV;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "DayZ Farm Controller");

builder.Services.AddOptions<DayZFarmOptions>()
    .Bind(builder.Configuration.GetSection(DayZFarmOptions.SectionName));

// Fail fast on invalid configuration rather than starting half-broken.
var configuredOptions = builder.Configuration.GetSection(DayZFarmOptions.SectionName).Get<DayZFarmOptions>() ?? new DayZFarmOptions();
DayZFarmOptionsValidator.ValidateAndThrow(configuredOptions);

// Structured, rolling-file logging under D:\DayZFarm\Logs\Controller\. Never logs secrets —
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

builder.Services.AddSingleton<IPowerShellRunner, ProcessPowerShellRunner>();
builder.Services.AddSingleton<IVirtualMachineProvider, HyperVVirtualMachineProvider>();
builder.Services.AddSingleton<IClientRepository, ClientRepository>();
builder.Services.AddSingleton<ISecretStore, SecretStore>();
builder.Services.AddSingleton<ConcurrencyGates>();
builder.Services.AddSingleton<AgentEndpointResolver>();
builder.Services.AddSingleton<ClientActionLogger>();
builder.Services.AddSingleton<IGpuVirtualizationProvider, ManualGpuVirtualizationProvider>();
// The Agent's own DayZ-launch polling loop blocks its HTTP response for up to ~30s waiting for
// the DayZ_x64 process to appear (see WindowsDayZLauncher.LaunchAsync) -- 15s was shorter than
// that and caused a real, reproducible TaskCanceledException ("Agent unreachable... 15 seconds
// elapsing") on Start DayZ/Join whenever the launch legitimately took close to or over 15s. See
// docs/TROUBLESHOOTING.md.
builder.Services.AddHttpClient<IAgentClient, AgentHttpClient>(c => c.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<ClientOrchestrator>();
builder.Services.AddHostedService<StatusPollingService>();

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
var farmOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<DayZFarmOptions>>().Value;
foreach (var dir in new[] { farmOptions.ImagesDirectory, farmOptions.InstancesDirectory, farmOptions.ConfigDirectory, farmOptions.ControllerLogsDirectory, farmOptions.ClientLogsDirectory, farmOptions.BackupsDirectory })
    Directory.CreateDirectory(dir);

app.UseSerilogRequestLogging();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapClientsApi();
app.MapHub<FarmStatusHub>("/hubs/farm-status");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
