using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Tests.Queue
{
    // The lane's queue and its poison queue, in memory, for the loop's tests. A message that is taken and not
    // deleted does not come back within the test, as in Azure it stays hidden for the visibility timeout.
    internal sealed class FakeScanQueue : IScanQueue
    {
        private readonly Queue<ScanQueueMessage> _waiting = new Queue<ScanQueueMessage>();

        public List<TimeSpan> VisibilityTimeouts { get; } = new List<TimeSpan>();

        public List<ScanQueueMessage> Deleted { get; } = new List<ScanQueueMessage>();

        public List<ScanQueueMessage> Poisoned { get; } = new List<ScanQueueMessage>();

        // Not taken yet
        public int WaitingCount => _waiting.Count;

        // dequeueCount is what Azure gives the message when it is taken: 1 the first time. insertedOn is when
        // Azure queued it; left null, the loop's "already done" check (Issue 13) never applies.
        public ScanQueueMessage Add(string body, long dequeueCount = 1, DateTimeOffset? insertedOn = null)
        {
            int number = _waiting.Count + Deleted.Count + Poisoned.Count + 1;
            ScanQueueMessage message = new ScanQueueMessage("message-" + number, "receipt-" + number, body, dequeueCount, insertedOn);
            _waiting.Enqueue(message);
            return message;
        }

        public Task<ScanQueueMessage?> ReceiveAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VisibilityTimeouts.Add(visibilityTimeout);

            ScanQueueMessage? message = _waiting.Count > 0 ? _waiting.Dequeue() : null;
            return Task.FromResult(message);
        }

        public Task DeleteAsync(ScanQueueMessage message, CancellationToken cancellationToken)
        {
            Deleted.Add(message);
            return Task.CompletedTask;
        }

        public Task MoveToPoisonAsync(ScanQueueMessage message, CancellationToken cancellationToken)
        {
            Poisoned.Add(message);
            return Task.CompletedTask;
        }
    }
}
