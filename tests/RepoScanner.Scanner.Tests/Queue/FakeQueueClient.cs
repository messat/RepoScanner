using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace RepoScanner.Scanner.Tests.Queue
{
    // A QueueClient that keeps its messages in memory and records what was asked of it, so QueueConsumer
    // is tested without Azurite. Azure's clients have a protected constructor and virtual methods for this.
    public sealed class FakeQueueClient : QueueClient
    {
        private readonly string _name;
        private readonly List<string> _calls;

        // calls is shared between the fakes of one test, so the order of calls across queues can be checked
        public FakeQueueClient(string name, List<string> calls)
        {
            _name = name;
            _calls = calls;
        }

        public override string Name => _name;

        // Given out one at a time, oldest first
        public Queue<QueueMessage> Messages { get; } = new Queue<QueueMessage>();

        public List<TimeSpan?> ReceiveVisibilityTimeouts { get; } = new List<TimeSpan?>();

        public List<(string MessageId, string PopReceipt)> Deleted { get; } = new List<(string MessageId, string PopReceipt)>();

        public List<(string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive)> Sent { get; } = new List<(string Text, TimeSpan? VisibilityTimeout, TimeSpan? TimeToLive)>();

        // When set, that call throws it instead of doing anything
        public RequestFailedException? ReceiveFailure { get; set; }

        public RequestFailedException? DeleteFailure { get; set; }

        public RequestFailedException? SendFailure { get; set; }

        public void Add(string messageId, string popReceipt, string body, long dequeueCount, DateTimeOffset? insertedOn = null)
        {
            Messages.Enqueue(QueuesModelFactory.QueueMessage(messageId, popReceipt, new BinaryData(body), dequeueCount, insertedOn: insertedOn));
        }

        public override Task<Response<QueueMessage>> ReceiveMessageAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
        {
            _calls.Add(_name + ": receive");
            if (ReceiveFailure != null)
            {
                throw ReceiveFailure;
            }

            ReceiveVisibilityTimeouts.Add(visibilityTimeout);

            // Azure gives a response whose value is null when the queue is empty
            QueueMessage? message = Messages.Count > 0 ? Messages.Dequeue() : null;
            return Task.FromResult(Response.FromValue(message!, new FakeResponse(200)));
        }

        public override Task<Response> DeleteMessageAsync(string messageId, string popReceipt, CancellationToken cancellationToken = default)
        {
            _calls.Add(_name + ": delete");
            if (DeleteFailure != null)
            {
                throw DeleteFailure;
            }

            Deleted.Add((messageId, popReceipt));
            return Task.FromResult<Response>(new FakeResponse(204));
        }

        public override Task<Response<SendReceipt>> SendMessageAsync(
            string messageText, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
        {
            _calls.Add(_name + ": send");
            if (SendFailure != null)
            {
                throw SendFailure;
            }

            Sent.Add((messageText, visibilityTimeout, timeToLive));
            SendReceipt receipt = QueuesModelFactory.SendReceipt(
                "sent-" + Sent.Count, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "receipt", DateTimeOffset.UtcNow);
            return Task.FromResult(Response.FromValue(receipt, new FakeResponse(201)));
        }
    }
}
