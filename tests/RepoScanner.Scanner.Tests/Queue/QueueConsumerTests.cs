using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Tests.Queue
{
    [TestFixture]
    public class QueueConsumerTests
    {
        private const string Body = "{\"schemaVersion\": 1}";

        private List<string> _calls = null!;
        private FakeQueueClient _queue = null!;
        private FakeQueueClient _poisonQueue = null!;
        private FakeLogger<QueueConsumer> _logger = null!;

        [SetUp]
        public void SetUp()
        {
            _calls = new List<string>();
            _queue = new FakeQueueClient("scan-nightly", _calls);
            _poisonQueue = new FakeQueueClient("scan-nightly-poison", _calls);
            _logger = new FakeLogger<QueueConsumer>();
        }

        [Test]
        public async Task ReceiveAsync_AsksForOneMessageHiddenForTheVisibilityTimeout()
        {
            _queue.Add("message-1", "receipt-1", Body, dequeueCount: 2);
            _queue.Add("message-2", "receipt-2", Body, dequeueCount: 1);

            ScanQueueMessage? message = await CreateConsumer().ReceiveAsync(TimeSpan.FromMinutes(65), CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(message, Is.EqualTo(new ScanQueueMessage("message-1", "receipt-1", Body, 2)));
                Assert.That(_queue.ReceiveVisibilityTimeouts, Is.EqualTo(new TimeSpan?[] { TimeSpan.FromMinutes(65) }));
                Assert.That(_queue.Messages, Has.Count.EqualTo(1), "only one message is taken");
            }
        }

        // Issue 13: the loop compares when the message was queued with when a scan run started
        [Test]
        public async Task ReceiveAsync_CarriesTheTimeTheMessageWasQueued()
        {
            DateTimeOffset queued = new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero);
            _queue.Add("message-1", "receipt-1", Body, dequeueCount: 1, insertedOn: queued);

            ScanQueueMessage? message = await CreateConsumer().ReceiveAsync(TimeSpan.FromMinutes(65), CancellationToken.None);

            Assert.That(message?.InsertedOn, Is.EqualTo(queued));
        }

        [Test]
        public async Task ReceiveAsync_WhenTheQueueIsEmpty_ReturnsNull()
        {
            ScanQueueMessage? message = await CreateConsumer().ReceiveAsync(TimeSpan.FromMinutes(65), CancellationToken.None);

            Assert.That(message, Is.Null);
        }

        [Test]
        public async Task DeleteAsync_DeletesByIdAndPopReceipt()
        {
            await CreateConsumer().DeleteAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 1), CancellationToken.None);

            Assert.That(_queue.Deleted, Is.EqualTo(new[] { ("message-1", "receipt-1") }));
        }

        // Another run took the message after it reappeared, or it is already gone: not a failure
        [TestCase(404, "MessageNotFound")]
        [TestCase(400, "PopReceiptMismatch")]
        public void DeleteAsync_WhenTheMessageIsNoLongerThisRuns_LogsAWarning(int status, string errorCode)
        {
            _queue.DeleteFailure = new RequestFailedException(status, "gone", errorCode, null);

            Assert.DoesNotThrowAsync(() => CreateConsumer().DeleteAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 1), CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_logger.LatestRecord.Level, Is.EqualTo(LogLevel.Warning));
                Assert.That(_logger.LatestRecord.GetStructuredStateValue("MessageId"), Is.EqualTo("message-1"));
            }
        }

        [Test]
        public void DeleteAsync_WhenAzureFailsForAnotherReason_Throws()
        {
            _queue.DeleteFailure = new RequestFailedException(500, "server busy", "ServerBusy", null);

            Assert.ThrowsAsync<RequestFailedException>(() => CreateConsumer().DeleteAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 1), CancellationToken.None));
        }

        [Test]
        public async Task MoveToPoisonAsync_SendsTheSameBodyToThePoisonQueueThenDeletesTheOriginal()
        {
            await CreateConsumer(poisonMessageTimeToLiveDays: 7).MoveToPoisonAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 4), CancellationToken.None);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_poisonQueue.Sent, Is.EqualTo(new[] { (Body, (TimeSpan?)null, (TimeSpan?)TimeSpan.FromDays(7)) }));
                Assert.That(_queue.Deleted, Is.EqualTo(new[] { ("message-1", "receipt-1") }));

                // Copied first, so the message can never be lost
                Assert.That(_calls, Is.EqualTo(new[] { "scan-nightly-poison: send", "scan-nightly: delete" }));
            }
        }

        [Test]
        public void MoveToPoisonAsync_WhenTheCopyFails_DeletesNothing()
        {
            _poisonQueue.SendFailure = new RequestFailedException(500, "server busy", "ServerBusy", null);

            Assert.ThrowsAsync<RequestFailedException>(() => CreateConsumer().MoveToPoisonAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 4), CancellationToken.None));

            Assert.That(_queue.Deleted, Is.Empty);
        }

        [Test]
        public void ReceiveAsync_WhenTheQueueDoesNotExist_ThrowsNamingIt()
        {
            _queue.ReceiveFailure = QueueNotFound();

            ScanQueueNotFoundException exception = Assert.ThrowsAsync<ScanQueueNotFoundException>(
                () => CreateConsumer().ReceiveAsync(TimeSpan.FromMinutes(65), CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.QueueName, Is.EqualTo("scan-nightly"));
                Assert.That(exception.Message, Does.Contain("scan-nightly"));
            }
        }

        [Test]
        public void DeleteAsync_WhenTheQueueDoesNotExist_ThrowsNamingIt()
        {
            _queue.DeleteFailure = QueueNotFound();

            ScanQueueNotFoundException exception = Assert.ThrowsAsync<ScanQueueNotFoundException>(
                () => CreateConsumer().DeleteAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 1), CancellationToken.None));

            Assert.That(exception.QueueName, Is.EqualTo("scan-nightly"));
        }

        [Test]
        public void MoveToPoisonAsync_WhenThePoisonQueueDoesNotExist_ThrowsNamingItAndKeepsTheOriginal()
        {
            _poisonQueue.SendFailure = QueueNotFound();

            ScanQueueNotFoundException exception = Assert.ThrowsAsync<ScanQueueNotFoundException>(
                () => CreateConsumer().MoveToPoisonAsync(new ScanQueueMessage("message-1", "receipt-1", Body, 4), CancellationToken.None));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(exception.QueueName, Is.EqualTo("scan-nightly-poison"));
                Assert.That(_queue.Deleted, Is.Empty);
            }
        }

        private static RequestFailedException QueueNotFound()
        {
            return new RequestFailedException(404, "The specified queue does not exist.", "QueueNotFound", null);
        }

        private QueueConsumer CreateConsumer(int poisonMessageTimeToLiveDays = 30)
        {
            QueueOptions options = new QueueOptions
            {
                Name = "scan-nightly",
                ConnectionString = "UseDevelopmentStorage=true",
                PoisonMessageTimeToLiveDays = poisonMessageTimeToLiveDays
            };

            return new QueueConsumer(_queue, _poisonQueue, Options.Create(options), _logger);
        }
    }
}
