using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Queue
{
    // IScanQueue over the lane's Azure queue and its poison queue (#30). Azure's "queue not found" becomes
    // a settings error naming the queue; every other Azure error is left to stop the run (Issue 7 design,
    // Queue access).
    public sealed class QueueConsumer : IScanQueue
    {
        private readonly QueueClient _queue;
        private readonly QueueClient _poisonQueue;
        private readonly TimeSpan _poisonTimeToLive;
        private readonly ILogger<QueueConsumer> _logger;

        public QueueConsumer(QueueClient queue, QueueClient poisonQueue, IOptions<QueueOptions> options, ILogger<QueueConsumer> logger)
        {
            _queue = queue;
            _poisonQueue = poisonQueue;
            _poisonTimeToLive = TimeSpan.FromDays(options.Value.PoisonMessageTimeToLiveDays);
            _logger = logger;
        }

        public async Task<ScanQueueMessage?> ReceiveAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
        {
            Response<QueueMessage> response;
            try
            {
                // One message only: a batch would hide every message in it while this run works through them
                // one by one, so another run could not help (Issue 7 design, decision 10)
                response = await _queue.ReceiveMessageAsync(visibilityTimeout, cancellationToken);
            }
            catch (RequestFailedException exception) when (IsQueueNotFound(exception))
            {
                throw new ScanQueueNotFoundException(_queue.Name, exception);
            }

            // Azure's value is null when the queue is empty
            QueueMessage? message = response.Value;
            if (message == null)
            {
                return null;
            }

            return new ScanQueueMessage(message.MessageId, message.PopReceipt, message.Body.ToString(), message.DequeueCount, message.InsertedOn);
        }

        public async Task DeleteAsync(ScanQueueMessage message, CancellationToken cancellationToken)
        {
            try
            {
                await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, cancellationToken);
            }
            catch (RequestFailedException exception) when (IsQueueNotFound(exception))
            {
                throw new ScanQueueNotFoundException(_queue.Name, exception);
            }
            catch (RequestFailedException exception)
                when (exception.ErrorCode == QueueErrorCode.MessageNotFound || exception.ErrorCode == QueueErrorCode.PopReceiptMismatch)
            {
                // The message reappeared and another run took it, or it is already gone. Not a failure: the
                // scan is saved, and at worst the repository is scanned again (Issue 7 design, decision 11).
                _logger.LogWarning(
                    "Message {MessageId} was no longer held by this run when deleting it from {QueueName}: another run took it or it was already deleted",
                    message.MessageId,
                    _queue.Name);
            }
        }

        public async Task MoveToPoisonAsync(ScanQueueMessage message, CancellationToken cancellationToken)
        {
            try
            {
                // The body unchanged, so it can be moved back with Storage Explorer's Move; visible at once,
                // and kept long enough to be noticed (Issue 7 design, decision 14)
                await _poisonQueue.SendMessageAsync(message.Body, visibilityTimeout: null, timeToLive: _poisonTimeToLive, cancellationToken: cancellationToken);
            }
            catch (RequestFailedException exception) when (IsQueueNotFound(exception))
            {
                throw new ScanQueueNotFoundException(_poisonQueue.Name, exception);
            }

            // Only after the copy is made, so a failure in between leaves the message in both queues, never neither
            await DeleteAsync(message, cancellationToken);
        }

        private static bool IsQueueNotFound(RequestFailedException exception)
        {
            return exception.ErrorCode == QueueErrorCode.QueueNotFound;
        }
    }
}
