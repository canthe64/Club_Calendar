using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

/// <summary>
/// BreelyWebhookOutstandingWork - the code-review C4 interim fix letting ApplicationStopping wait
/// for detached webhook processing rather than the app process ending mid-batch.
/// </summary>
public class BreelyWebhookOutstandingWorkTests
{
    [Fact]
    public async Task WaitForAllAsync_NothingTracked_ReturnsImmediately()
    {
        var work = new BreelyWebhookOutstandingWork();

        var task = work.WaitForAllAsync(TimeSpan.FromSeconds(10));
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));

        Assert.Same(task, completed);
    }

    [Fact]
    public async Task WaitForAllAsync_WaitsForATrackedTaskToComplete()
    {
        var work = new BreelyWebhookOutstandingWork();
        var tcs = new TaskCompletionSource();
        work.Track(tcs.Task);

        var waitTask = work.WaitForAllAsync(TimeSpan.FromSeconds(10));
        // Not yet complete - the tracked task hasn't finished.
        var prematurelyDone = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(waitTask, prematurelyDone);

        tcs.SetResult();

        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(waitTask, completed);
    }

    [Fact]
    public async Task WaitForAllAsync_ATaskThatNeverCompletes_ReturnsAfterTheTimeoutInstead()
    {
        // A genuinely stuck task must not hang shutdown itself - the whole point of the bounded
        // wait, since ApplicationStopping only gets one grace period to finish everything it does.
        var work = new BreelyWebhookOutstandingWork();
        var neverCompletes = new TaskCompletionSource();
        work.Track(neverCompletes.Task);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await work.WaitForAllAsync(TimeSpan.FromMilliseconds(200));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected the bounded wait to return promptly; took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task Track_RemovesTheTaskOnceItCompletes()
    {
        var work = new BreelyWebhookOutstandingWork();
        var tcs = new TaskCompletionSource();
        work.Track(tcs.Task);
        Assert.Equal(1, work.OutstandingCount);

        tcs.SetResult();
        await tcs.Task; // ensure the continuation has a chance to run

        // The removal continuation is scheduled, not synchronous with the task completing - poll
        // briefly rather than asserting immediately after await, which races it.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (work.OutstandingCount != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, work.OutstandingCount);
    }

    [Fact]
    public void Track_MultipleOutstandingTasks_AllCounted()
    {
        var work = new BreelyWebhookOutstandingWork();
        work.Track(new TaskCompletionSource().Task);
        work.Track(new TaskCompletionSource().Task);

        Assert.Equal(2, work.OutstandingCount);
    }
}
