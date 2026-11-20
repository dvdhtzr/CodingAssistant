using Microsoft.AspNetCore.SignalR;

namespace CodingAssistant.Api;

/// <summary>
/// Authenticated local notification hub. Authoritative history stays in SQLite.
/// Subscribe FIRST, then fetch /api/runs/{id}/events?after=cursor to close
/// the snapshot/subscribe race. Deduplicate by (runId, sequence).
/// </summary>
public sealed class RunProgressHub(RunStore runs) : Hub
{
    internal static string GroupName(string runId) => "run:" + runId;

    public async Task<long> WatchRun(string runId)
    {
        // Validates ID and existence. The sole local owner may observe every
        // run; the /api middleware authenticates the WebSocket handshake.
        var current = runs.Get(runId);
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(runId));
        return current.LastSequence;
    }

    public Task UnwatchRun(string runId)
    {
        // Reject invalid IDs, including crafted group names.
        runs.Get(runId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(runId));
    }
}

/// <summary>
/// Best-effort hint after a committed SQLite transaction. Never imply that
/// a SignalR send constitutes durable execution/evidence; clients always
/// replay from SQLite after reconnect. No source, prompt or model data sent.
/// </summary>
public sealed class RunNotifier(IHubContext<RunProgressHub> hub)
{
    public async Task PublishAsync(RunEvent? change)
    {
        if (change is null) return;
        try
        {
            await hub.Clients.Group(RunProgressHub.GroupName(change.RunId))
                .SendAsync("RunUpdated", change);
        }
        catch (Exception)
        {
            // A disconnected/unavailable hub cannot roll back a committed
            // DB event and cannot transform a successful write into an error.
            // /events?after= remains the durable recovery mechanism.
        }
    }
}
