using System;
using Azure.Storage.Queues;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Tests.Queue
{
    [TestFixture]
    public class QueueClientsTests
    {
        // Building a client makes no network call, so neither case needs Azurite or Azure
        [Test]
        public void Create_WithAConnectionString_PointsAtThatQueueInAzurite()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true" };

            QueueClient client = QueueClients.Create(options, "scan-nightly-poison");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(client.Uri, Is.EqualTo(new Uri("http://127.0.0.1:10001/devstoreaccount1/scan-nightly-poison")));
                Assert.That(client.Name, Is.EqualTo("scan-nightly-poison"));
            }
        }

        [Test]
        public void Create_WithAServiceUri_PointsAtThatQueueInTheAccount()
        {
            QueueOptions options = new QueueOptions { Name = "scan-ondemand", ServiceUri = new Uri("https://example.queue.core.windows.net") };

            QueueClient client = QueueClients.Create(options, "scan-ondemand");

            Assert.That(client.Uri, Is.EqualTo(new Uri("https://example.queue.core.windows.net/scan-ondemand")));
        }
    }
}
