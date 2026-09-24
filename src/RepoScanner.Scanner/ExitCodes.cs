namespace RepoScanner.Scanner
{
    // Process exit codes. The Container Apps Job treats 0 as success and anything else as a failed execution.
    public static class ExitCodes
    {
        // The queue was processed, or there was nothing to do
        public const int Success = 0;

        // Something unexpected went wrong during the run
        public const int Failure = 1;

        // Settings are missing or invalid, so the scanner did not start
        public const int InvalidConfiguration = 2;
    }
}
