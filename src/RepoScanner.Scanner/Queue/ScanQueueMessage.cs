namespace RepoScanner.Scanner.Queue
{
    // One message taken from the lane's queue. The pop receipt is what proves this run still holds it
    // when it is deleted. DequeueCount is how many times it has been taken, this time included (ADR-013).
    public sealed record ScanQueueMessage(string MessageId, string PopReceipt, string Body, long DequeueCount);
}
