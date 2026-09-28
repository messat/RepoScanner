using System;

namespace RepoScanner.Scanner.Packages
{
    // nuget.org could not be reached, or sent something that could not be read, after the retries. The package
    // scanner fails on its own and the run is PartiallyCompleted (#74, #32). The message says which address
    // failed and why, and never quotes a response, because it is saved as the scanner's ErrorMessage.
    public sealed class NuGetUnavailableException : Exception
    {
        public NuGetUnavailableException(string message)
            : base(message)
        {
        }

        public NuGetUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
