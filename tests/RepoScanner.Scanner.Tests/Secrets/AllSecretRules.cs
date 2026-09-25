using RepoScanner.Scanner.Secrets;
using RepoScanner.Scanner.Secrets.Rules;

namespace RepoScanner.Scanner.Tests.Secrets
{
    // A new instance of each of the seven rules, in ID order, for tests that need all of them
    internal static class AllSecretRules
    {
        public static ISecretRule[] Create()
        {
            return new ISecretRule[]
            {
                new Sec001PrivateKeyRule(),
                new Sec002AzureSharedKeyRule(),
                new Sec003ConnectionStringPasswordRule(),
                new Sec004UrlCredentialsRule(),
                new Sec005EntraClientSecretRule(),
                new Sec006NuGetApiKeyRule(),
                new Sec007NamedSettingRule()
            };
        }
    }
}
