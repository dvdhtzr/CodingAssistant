using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;

namespace CodingAssistant.Api;

/// <summary>Bounded, best-effort post-commit project hints. Durable SQLite
/// replay is authoritative; dropped hints cannot lose activity history.</summary>
public sealed class EngineeringActivityNotifier(TaskStore tasks,
    IHubContext<RunProgressHub> hub) : BackgroundService
{
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(
        new BoundedChannelOptions(256) { SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite });

    private void Signal(string taskId) => _pending.Writer.TryWrite(taskId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        tasks.EngineeringActivityChanged += Signal;
        try
        {
            while (await _pending.Reader.WaitToReadAsync(stoppingToken))
            {
                await Task.Delay(100,stoppingToken);
                var changed = new HashSet<string>(StringComparer.Ordinal);
                for (var count=0; count<256 && _pending.Reader.TryRead(out var taskId); count++)
                    changed.Add(taskId);
                foreach (var changedTask in changed)
                {
                    try
                    {
                        await hub.Clients.Group(RunProgressHub.EngineeringGroupName(changedTask))
                            .SendAsync("EngineeringActivityUpdated",new { taskId = changedTask },stoppingToken);
                    }
                    catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { tasks.EngineeringActivityChanged -= Signal; }
    }
}
