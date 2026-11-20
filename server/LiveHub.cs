using Microsoft.AspNetCore.SignalR;

namespace Fireside;

// Clients can only listen. State changes always go through authorized, antiforgery-protected APIs.
public sealed class LiveHub : Hub
{
}

public sealed class LiveUpdates(IHubContext<LiveHub> hub, ILogger<LiveUpdates> logger)
{
    public async Task Notify(LiveReaction? reaction = null)
    {
        try
        {
            var changed = hub.Clients.All.SendAsync("boardChanged");
            if (reaction is null) await changed;
            else await Task.WhenAll(changed, hub.Clients.All.SendAsync("reaction", reaction));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A committed write must not become a failed request; clients also reconcile periodically.
            logger.LogError(exception, "Live notification failed after a committed board update");
        }
    }
}
