using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AudioHaus.Interop;

namespace AudioHaus.App;

/*
 * Name: AudioService
 * Input: Serialized graph operations and discovery requests.
 * Process: Owns the router on one dedicated MTA thread and drains accepted work before disposal.
 * Output: Task results without native calls on the UI dispatcher.
 */
internal sealed class AudioService
{
    private readonly BlockingCollection<(Action Execute, Action<Exception> Reject)> queue = new();
    private readonly object lifetime = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread worker;
    private AudioRouter? router;
    private bool closing;

    /*
     * Name: AudioService
     * Input: None.
     * Process: Starts a background MTA worker; native initialization remains asynchronous.
     * Output: An empty service ready to accept work.
     */
    public AudioService()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "AudioHaus managed audio service" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
    }

    /*
     * Name: InitializeAsync
     * Input: None.
     * Process: Creates the native graph on its managed owner thread.
     * Output: Completion or the native startup exception.
     */
    public Task InitializeAsync() => Enqueue(() => { router ??= AudioRouter.Create(); return true; });

    /*
     * Name: ExecuteAsync
     * Input: A graph operation that must not access UI objects.
     * Process: Serializes the operation behind previously accepted work.
     * Output: Its result or exception; shutdown rejects new operations.
     */
    public Task<T> ExecuteAsync<T>(Func<AudioRouter, T> operation) => Enqueue(() =>
        operation(router ?? throw new InvalidOperationException("Audio service is not initialized.")));

    /*
     * Name: ExecuteAsync
     * Input: A graph mutation that must not access UI objects.
     * Process: Serializes the mutation on the MTA worker.
     * Output: Completion or the operation exception.
     */
    public Task ExecuteAsync(Action<AudioRouter> operation) => ExecuteAsync(router => { operation(router); return true; });

    /*
     * Name: DiscoverAsync
     * Input: None.
     * Process: Enumerates endpoints and active application sessions off the UI thread.
     * Output: Endpoint snapshots plus sessions, retaining endpoint results if session discovery fails.
     */
    public Task<DiscoverySnapshot> DiscoverAsync() => Enqueue(() =>
    {
        IReadOnlyList<AudioEndpoint> endpoints = new AudioEndpointEnumerator().Enumerate();
        try { return new DiscoverySnapshot(endpoints, new AudioApplicationEnumerator().Enumerate(endpoints), null); }
        catch (Exception error) { return new DiscoverySnapshot(endpoints, Array.Empty<AudioApplicationSession>(), error.Message); }
    });

    /*
     * Name: ShutdownAsync
     * Input: None.
     * Process: Rejects new work, drains accepted requests, disposes the router, and exits the worker.
     * Output: One shared completion task; repeated calls are harmless.
     */
    public Task ShutdownAsync()
    {
        lock (lifetime)
        {
            if (closing) { return stopped.Task; }
            closing = true;
            queue.CompleteAdding();
        }
        return stopped.Task;
    }

    /*
     * Name: Enqueue
     * Input: Synchronous worker operation.
     * Process: Atomically accepts work before shutdown and observes exceptions per request.
     * Output: A task containing its result or failure.
     */
    private Task<T> Enqueue<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (lifetime)
        {
            if (closing) { return Task.FromException<T>(new ObjectDisposedException(nameof(AudioService))); }
            try
            {
                queue.Add((() =>
                {
                    try { completion.TrySetResult(operation()); }
                    catch (Exception error) { completion.TrySetException(error); }
                }, error => completion.TrySetException(error)));
            }
            catch (Exception error) { completion.TrySetException(error); }
        }
        return completion.Task;
    }

    /*
     * Name: Run
     * Input: Accepted work queue.
     * Process: Executes tasks sequentially and releases native ownership on the same MTA thread.
     * Output: Worker completion after cleanup; disposal failures fault shutdown.
     */
    private void Run()
    {
        Exception? failure = null;
        try { foreach (var work in queue.GetConsumingEnumerable()) { work.Execute(); } }
        catch (Exception error) { failure = error; }
        finally
        {
            lock (lifetime)
            {
                closing = true;
                queue.CompleteAdding();
            }
            Exception rejection = failure ?? new ObjectDisposedException(nameof(AudioService));
            while (queue.TryTake(out var abandoned)) { abandoned.Reject(rejection); }
            try { router?.Dispose(); }
            catch (Exception error) { failure ??= error; }
            router = null;
            queue.Dispose();
            if (failure is null) { stopped.TrySetResult(); }
            else { stopped.TrySetException(failure); }
        }
    }
}

/*
 * Name: DiscoverySnapshot
 * Input: Copied endpoint and application information and optional session failure.
 * Process: Carries discovery data across the worker/UI boundary without native ownership.
 * Output: Immutable discovery results.
 */
internal sealed record DiscoverySnapshot(IReadOnlyList<AudioEndpoint> Endpoints,
    IReadOnlyList<AudioApplicationSession> Applications, string? ApplicationError);
