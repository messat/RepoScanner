using System;

namespace RepoScanner.Scanner.Sources
{
    // Azure DevOps refused the PAT: it is wrong, expired or revoked, or lacks Code (Read). Every other repository would
    // fail the same way, so the queue loop stops and Program exits 1, leaving the messages (Issue 8 design, decision 13).
    public sealed class AzureDevOpsAuthenticationException : Exception
    {
        public AzureDevOpsAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
