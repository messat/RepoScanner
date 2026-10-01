using System;

namespace RepoScanner.Scanner.Sources
{
    // A repository's clone address, built from the organisation in settings and the project and repository in the
    // queue message (Issue 8 design, The URL). Never with a user name or token in it (ADR-007, ADR-016).
    public static class CloneUrl
    {
        // <OrganizationUrl>/<project>/_git/<repository>. Each name is escaped, so "Kudos Wall" becomes Kudos%20Wall
        // and a & or # cannot end the path early. Git decodes the escapes in file:/// URLs too, as the tests use.
        public static string For(string organizationUrl, string projectName, string repositoryName)
        {
            return organizationUrl.TrimEnd('/') + "/" + Uri.EscapeDataString(projectName) + "/_git/" + Uri.EscapeDataString(repositoryName);
        }
    }
}
