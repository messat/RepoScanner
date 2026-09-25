namespace Fixture.Api
{
    // Fake values for RepoScanner's tests: none of them has ever worked anywhere
    public static class StorageSettings
    {
        public const string Connection =
            "DefaultEndpointsProtocol=https;AccountName=fakeaccount;AccountKey=FAKEstorageAccountKeyForRepoScannerTests0000000000000000000000000000000000000000000000==;EndpointSuffix=core.windows.net";

        public const string GraphClientSecret = "FAK8Q~FAKE.client_secret~for-RepoScanner";
    }
}
