using System;

namespace RepoScanner.Scanner.Queue
{
    // One message taken from the lane's queue. The pop receipt is what proves this run still holds it
    // when it is deleted. DequeueCount is how many times it has been taken, this time included (ADR-013).
    // InsertedOn is when Azure queued it: a scan run of the same lane that started after that has already done
    // what the message asks, so the loop deletes it (Issue 13). Null means not known, so it is scanned.
    public sealed record ScanQueueMessage(string MessageId, string PopReceipt, string Body, long DequeueCount, DateTimeOffset? InsertedOn = null);
}
