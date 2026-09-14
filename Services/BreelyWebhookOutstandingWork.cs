using System.Collections.Concurrent;

namespace FacilityScheduler.Services;

/// <summary>
/// Tracks Breely webhook processing tasks that run detached from their originating request
/// (architecture doc §4.8/§5.5, D49 - ack fast, process on CancellationToken.None so an HTTP timeout
/// can't abort a write mid-sequence). D49's own reasoning covers an HTTP-level timeout; it doesn't
/// cover the app process itself going away - an App Service recycle, deploy swap, or scale-in ends
/// the process the same way an ungraceful kill would, abandoning whatever's mid-flight (code review
/// C4). This is the interim fix: register outstanding work here so <c>ApplicationStopping</c> can
/// await it during the shutdown grace period, instead of a full <c>Channel&lt;T&gt;</c>+
/// <c>BackgroundService</c> queue (the more durable fix, also covers a hard kill mid-batch, not
/// taken here - accepted as a known remaining gap, not this interim fix's job).
///
/// Singleton (Program.cs) - the tasks it tracks outlive any one request's own DI scope, same
/// lifetime reasoning as everything <c>BreelyBookingProcessor</c> itself depends on.
/// </summary>
public class BreelyWebhookOutstandingWork
{
    private readonly ConcurrentDictionary<Task, byte> _outstanding = new();

    // internal, not private - InternalsVisibleTo (D60 precedent), so tests can observe that a
    // completed task actually gets removed rather than accumulating for the life of the process.
    internal int OutstandingCount => _outstanding.Count;

    /// <summary>Registers a detached task to be waited on at shutdown. Must stay synchronous - called
    /// from the webhook endpoint's own fire-and-forget dispatch, which can't await this itself
    /// without reintroducing the exact HTTP-timeout coupling D49 removed.</summary>
    public void Track(Task task)
    {
        _outstanding[task] = 0;
        // Self-removing once done, via ContinueWith rather than awaiting here (Track must stay
        // synchronous) - keeps this from growing unbounded over the app's lifetime between
        // ApplicationStopping calls, which in the normal case never happen at all.
        task.ContinueWith(static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _outstanding, TaskScheduler.Default);
    }

    /// <summary>Awaits every currently-outstanding tracked task, bounded by <paramref name="timeout"/>
    /// so one genuinely stuck task can't hang shutdown indefinitely - called from
    /// <c>ApplicationStopping</c>. A snapshot of what's outstanding at the moment this is called;
    /// anything Track()ed after this point (a webhook delivery arriving mid-shutdown) is not waited
    /// on, consistent with the platform having already begun tearing the app down.</summary>
    public async Task WaitForAllAsync(TimeSpan timeout)
    {
        var snapshot = _outstanding.Keys.ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        await Task.WhenAny(Task.WhenAll(snapshot), Task.Delay(timeout));
    }
}
