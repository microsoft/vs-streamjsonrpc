using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;

internal class RpcOrderPreservingSynchronizationContext : SynchronizationContext, IDisposable
{
    /// <summary>
    /// The queue of work to execute.
    /// </summary>
    private readonly AsyncQueue<(SendOrPostCallback, object)> queue = new AsyncQueue<(SendOrPostCallback, object)>();

    private AsyncManualResetEvent postQueueFilled = new AsyncManualResetEvent(false);

    /// <summary>
    /// Initializes a new instance of the <see cref="RpcOrderPreservingSynchronizationContext"/> class.
    /// </summary>
    public RpcOrderPreservingSynchronizationContext()
    {
        // Start the queue processor. It will handle all exceptions.
        this.ProcessQueueAsync().Forget();
    }

    /// <summary>
    /// Occurs when posted work throws an unhandled exception.
    /// </summary>
    public event EventHandler<Exception>? UnhandledException;

    public int ExpectedTasksQueued
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void Post(SendOrPostCallback d, object state)
    {
        this.queue.Enqueue((d, state));

        // When the expected number of items in the queue has been met, we signal the queue filled event.
        if (this.queue.Count == this.ExpectedTasksQueued)
        {
            this.postQueueFilled.Set();
        }
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="d">The delegate to invoke.</param>
    /// <param name="state">State to pass to the delegate.</param>
    public override void Send(SendOrPostCallback d, object state) => throw new NotSupportedException();

    /// <summary>
    /// Throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <returns>Nothing.</returns>
    public override SynchronizationContext CreateCopy() => throw new NotSupportedException();

    /// <summary>
    /// Causes this <see cref="SynchronizationContext"/> to reject all future posted work and
    /// releases the queue processor when it is empty.
    /// </summary>
    public void Dispose() => this.queue.Complete();

    /// <summary>
    /// Executes queued work on the threadpool, one at a time.
    /// </summary>
    /// <returns>A task that always completes successfully.</returns>
    private async Task ProcessQueueAsync()
    {
        try
        {
            // Wait until all the expected items in the queue have been added.
            await this.postQueueFilled.WaitAsync();

            while (!this.queue.IsCompleted)
            {
                var work = await this.queue.DequeueAsync().ConfigureAwait(false);
                try
                {
                    work.Item1(work.Item2);
                }
                catch (Exception ex)
                {
                    this.UnhandledException?.Invoke(this, ex);
                }
            }
        }
        catch (OperationCanceledException) when (this.queue.IsCompleted)
        {
            // Successful shutdown.
        }
        catch (Exception ex)
        {
            // A failure to schedule work is fatal because it can lead to hangs that are
            // very hard to diagnose to a failure in the scheduler, and even harder to identify
            // the root cause of the failure in the scheduler.
            Environment.FailFast("Failure in scheduler.", ex);
        }
    }
}
