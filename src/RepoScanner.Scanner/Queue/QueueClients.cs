using System;
using Azure.Identity;
using Azure.Storage.Queues;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Queue
{
    // Builds the client for one of the lane's queues from the validated queue settings (#30)
    public static class QueueClients
    {
        public static QueueClient Create(QueueOptions options, string queueName)
        {
            // Messages are plain JSON, not Base64 (Issue 7 design, decision 3). None is the SDK's default;
            // it is set here so that it cannot change unnoticed.
            QueueClientOptions clientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.None };

            if (options.ServiceUri != null)
            {
                // Azure: no secret, the job's managed identity (ADR-007). AZURE_CLIENT_ID picks id-scanner (ADR-014).
                Uri queueUri = new QueueUriBuilder(options.ServiceUri) { QueueName = queueName }.ToUri();
                return new QueueClient(queueUri, new DefaultAzureCredential(), clientOptions);
            }

            // Local: Azurite's connection string. QueueOptions requires one of the two, so this is only
            // reached without one if the settings were never validated.
            string connectionString = options.ConnectionString
                ?? throw new InvalidOperationException("Scanner:Queue:ConnectionString or Scanner:Queue:ServiceUri must be set.");
            return new QueueClient(connectionString, queueName, clientOptions);
        }
    }
}
