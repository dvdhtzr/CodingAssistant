using Microsoft.AspNetCore.SignalR;

namespace CodingAssistant.Api;

/// <summary>
/// Authenticated local notification hub. Authoritative history stays in SQLite.
/// Subscribe FIRST, then fetch /api/runs/{id}/events?after=cursor to close
/// the snapshot/subscribe race. Deduplicate by (runId, sequence).
/// </summary>
public sealed class RunProgressHub(RunStore runs, EngineeringAtlasLedger atlas) : Hub
{
    internal static string GroupName(string runId) => "run:" + runId;
    internal static string AtlasGroupName(string runId) => "atlas:" + runId;

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

    public async Task<int> WatchAtlas(string taskId, string runId)
    {
        // Validates BOTH owning project and engineering run before admission.
        var page = atlas.Read(taskId, runId, 0);
        await Groups.AddToGroupAsync(Context.ConnectionId, AtlasGroupName(runId));
        return page.LastSequence;
    }

    public Task UnwatchAtlas(string taskId, string runId)
    {
        _ = atlas.Read(taskId, runId, 0);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, AtlasGroupName(runId));
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


/// <summary>
/// A low-latency hint, never authoritative source content or verification.
/// SQLite atlas-events is the durable record for missed messages or restart.
/// </summary>
public sealed class EngineeringAtlasNotifier(IHubContext<RunProgressHub> hub)
{
    public async Task PublishAsync(AtlasSourceEvent? change)
    {
        if (change is null) return;
        try
        {
            await hub.Clients.Group(RunProgressHub.AtlasGroupName(change.RunId))
                .SendAsync("AtlasUpdated", new
                {
                    change.RunId,
                    change.Sequence,
                    change.SourceRevision
                });
        }
        catch (Exception)
        {
            // A disconnected browser cannot invalidate persisted source.
            // Clients recover from /atlas-events?after=<last sequence>.
        }
    }
}
