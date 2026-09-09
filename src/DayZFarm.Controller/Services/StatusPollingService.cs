using DayZFarm.Controller.Hubs;
using DayZFarm.Core.Configuration;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DayZFarm.Controller.Services;

/// <summary>Periodically refreshes VM/agent status for every client and pushes it to connected dashboards.</summary>
public sealed class StatusPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<FarmStatusHub> _hub;
    private readonly DayZFarmOptions _options;
    private readonly ILogger<StatusPollingService> _logger;

    public StatusPollingService(
        IServiceScopeFactory scopeFactory,
        IHubContext<FarmStatusHub> hub,
        IOptions<DayZFarmOptions> options,
        ILogger<StatusPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<ClientOrchestrator>();
                var views = await orchestrator.GetAllViewsAsync(stoppingToken);
                var summary = ClientOrchestrator.Summarize(views);

                await _hub.Clients.All.SendAsync("ClientsUpdated", views, stoppingToken);
                await _hub.Clients.All.SendAsync("SummaryUpdated", summary, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Status polling iteration failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.StatusPollSeconds), stoppingToken);
        }
    }
}
