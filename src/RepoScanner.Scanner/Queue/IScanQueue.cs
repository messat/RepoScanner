using System;
using System.Threading;
using System.Threading.Tasks;

namespace RepoScanner.Scanner.Queue
{
    // The lane's queue and its poison queue (#30). QueueConsumer is the Azure one; the loop's tests use
    // one kept in memory.
    public interface IScanQueue
    {
        // One message, hidden from other runs for visibilityTimeout; null when the queue is empty
        Task<ScanQueueMessage?> ReceiveAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken);

        // Only once the message's scan run is saved (ADR-013)
        Task DeleteAsync(ScanQueueMessage message, CancellationToken cancellationToken);

        // Copies the body unchanged to the poison queue, kept for PoisonMessageTimeToLiveDays, then deletes the original
        Task MoveToPoisonAsync(ScanQueueMessage message, CancellationToken cancellationToken);
    }
}
