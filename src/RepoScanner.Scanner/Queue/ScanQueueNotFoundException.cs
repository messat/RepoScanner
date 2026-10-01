using System;

namespace RepoScanner.Scanner.Queue
{
    // One of the lane's queues does not exist. A settings problem, like a missing setting: Program logs it
    // as one line and exits 2. The app never creates queues: in Azure its identity may only use them (ADR-014).
    public sealed class ScanQueueNotFoundException : Exception
    {
        public ScanQueueNotFoundException(string queueName, Exception innerException)
            : base("The queue " + queueName + " does not exist. Create it, or correct Scanner:Queue:Name or Scanner:Queue:PoisonQueueName.", innerException)
        {
            QueueName = queueName;
        }

        public string QueueName { get; }
    }
}
