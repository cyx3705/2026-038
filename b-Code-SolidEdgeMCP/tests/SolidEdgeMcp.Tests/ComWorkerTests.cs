using System.Text.Json.Nodes;

namespace SolidEdgeMcp.Tests;

public sealed class ComWorkerTests
{
    private sealed class Backend
    {
        public int CreatedOn { get; } = Environment.CurrentManagedThreadId;
        public int? ClosedOn { get; set; }
        public ManualResetEventSlim Release { get; } = new();
        public int Calls { get; set; }

        public JsonNode Identity() => new JsonObject
        {
            ["thread"] = Environment.CurrentManagedThreadId,
            ["sta"] = Thread.CurrentThread.GetApartmentState() == ApartmentState.STA,
        };

        public JsonNode Slow()
        {
            Calls++;
            Assert.True(Release.Wait(TimeSpan.FromSeconds(3)));
            return new JsonObject { ["finished"] = true };
        }
    }

    private static ComWorker<Backend> Worker(Func<Backend> factory, double timeoutSeconds = 120) =>
        new(factory, backend => backend.ClosedOn = Environment.CurrentManagedThreadId, TimeSpan.FromSeconds(timeoutSeconds));

    [Fact]
    public async Task LifecycleRunsOnOneOtherStaThread()
    {
        var instances = new List<Backend>();
        var worker = Worker(() =>
        {
            var backend = new Backend();
            instances.Add(backend);
            return backend;
        });
        int thread;
        try
        {
            var first = await worker.CallAsync(b => b.Identity());
            var second = await worker.CallAsync(b => b.Identity());
            Assert.True(JsonNode.DeepEquals(first, second));
            thread = (int)first["thread"]!;
            Assert.NotEqual(Environment.CurrentManagedThreadId, thread);
            Assert.Equal(instances[0].CreatedOn, thread);
            Assert.True((bool)first["sta"]!);
            var error = await Assert.ThrowsAsync<EdgeError>(() =>
                worker.CallAsync(_ => throw new InvalidOperationException("backend failure")));
            Assert.Contains("backend failure", error.Message);
            Assert.True(JsonNode.DeepEquals(first, await worker.CallAsync(b => b.Identity())));
        }
        finally
        {
            worker.Dispose();
        }
        Assert.Single(instances);
        Assert.Equal(thread, instances[0].ClosedOn);
        var stopped = await Assert.ThrowsAsync<EdgeError>(() => worker.CallAsync(b => b.Identity()));
        Assert.Contains("stopped", stopped.Message);
    }

    [Fact]
    public async Task TimeoutDoesNotRetryOrCancelAWrite()
    {
        var backend = new Backend();
        var worker = Worker(() => backend, timeoutSeconds: 0.05);
        try
        {
            var timedOut = await Assert.ThrowsAsync<EdgeError>(() => worker.CallAsync(b => b.Slow()));
            Assert.Contains("may still complete", timedOut.Message);
            var busy = await Assert.ThrowsAsync<EdgeError>(() => worker.CallAsync(b => b.Identity()));
            Assert.Contains("previous call", busy.Message);
            Assert.Equal(1, backend.Calls);
            backend.Release.Set();
            await worker.Pending!.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotNull((await worker.CallAsync(b => b.Identity()))["thread"]);
            Assert.Equal(1, backend.Calls);
        }
        finally
        {
            backend.Release.Set();
            worker.Dispose();
        }
    }

    [Fact]
    public async Task NonFiniteResultDoesNotCrossWorkerBoundary()
    {
        using var worker = Worker(() => new Backend());
        var error = await Assert.ThrowsAsync<EdgeError>(() =>
            worker.CallAsync(_ => new JsonObject { ["volume"] = double.NaN }));
        Assert.Contains("JSON serializable", error.Message);
    }

    [Fact]
    public async Task StartupFailureIsReturnedToAllCalls()
    {
        using var worker = Worker(() => throw new InvalidOperationException("no COM runtime"));
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<EdgeError>(() => worker.CallAsync(b => b.Identity()));
            Assert.Contains("no COM runtime", error.Message);
        }
    }
}
