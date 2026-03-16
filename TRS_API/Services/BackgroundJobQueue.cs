namespace TRS_API.Services
{
    using System.Threading.Channels;

    public interface IBackgroundJobQueue
    {
        ValueTask EnqueueAsync(Func<CancellationToken, Task> job);
        ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct);
    }

    public class BackgroundJobQueue : IBackgroundJobQueue
    {
        private readonly Channel<Func<CancellationToken, Task>> _queue;

        public BackgroundJobQueue()
        {
            _queue = Channel.CreateUnbounded<Func<CancellationToken, Task>>();
        }

        public async ValueTask EnqueueAsync(Func<CancellationToken, Task> job)
        {
            await _queue.Writer.WriteAsync(job);
        }

        public async ValueTask<Func<CancellationToken, Task>> DequeueAsync(CancellationToken ct)
        {
            return await _queue.Reader.ReadAsync(ct);
        }
    }

}
