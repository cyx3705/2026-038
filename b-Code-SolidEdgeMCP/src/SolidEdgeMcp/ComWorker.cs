using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SolidEdgeMcp;

/// <summary>Keep all COM proxies and calls on one long-lived STA thread.</summary>
public sealed class ComWorker<TBackend>(Func<TBackend> factory, Action<TBackend> shutdown, TimeSpan timeout)
    : IDisposable where TBackend : class
{
    private sealed record Job(Func<TBackend, JsonNode> Operation, TaskCompletionSource<JsonNode> Completion);

    private readonly BlockingCollection<Job> _jobs = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private bool _closed;

    internal Task<JsonNode>? Pending { get; private set; }

    public async Task<JsonNode> CallAsync(Func<TBackend, JsonNode> operation)
    {
        Task<JsonNode> task;
        lock (_gate)
        {
            if (_closed)
                throw new EdgeError("The COM worker has stopped. Restart the MCP server.");
            if (Pending is { IsCompleted: false })
                throw new EdgeError("Solid Edge is still processing the previous call. Do not retry writes.");
            if (_thread is null)
            {
                _thread = new Thread(Run) { Name = "solid-edge-sta", IsBackground = true };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            var completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            task = Pending = completion.Task;
            _jobs.Add(new Job(operation, completion));
        }
        try
        {
            // A timeout only stops waiting; an already dispatched COM mutation keeps running.
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new EdgeError(
                "Solid Edge call timed out; it may still complete. Inspect for a CAD dialog, "
                + "then query the document before retrying any write.");
        }
    }

    private void Run()
    {
        TBackend? backend = null;
        string? startupError = null;
        try
        {
            backend = factory();
        }
        catch (Exception exc)
        {
            startupError = exc.Message;
        }
        try
        {
            // Managed waits on an STA thread pump COM messages, so idle time keeps the apartment alive.
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                try
                {
                    if (startupError is not null)
                        throw new EdgeError(startupError);
                    var result = job.Operation(backend!);
                    // Reject NaN/Infinity before the result leaves the apartment.
                    try
                    {
                        result.ToJsonString();
                    }
                    catch (ArgumentException exc)
                    {
                        throw new EdgeError($"Result is not JSON serializable: {exc.Message}");
                    }
                    job.Completion.SetResult(result);
                }
                catch (Exception exc)
                {
                    // Do not carry COM references in exception objects across apartments.
                    job.Completion.SetException(new EdgeError(Describe(exc)));
                }
            }
        }
        finally
        {
            if (backend is not null)
                shutdown(backend);
            backend = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static string Describe(Exception exc) =>
        exc is COMException com ? $"COM 0x{com.HResult:X8}: {com.Message}" : exc.Message;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _jobs.CompleteAdding();
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
