using NUnit.Framework;
using RepoScanner.Scanner.Sources;

namespace RepoScanner.Scanner.Tests.Sources
{
    [TestFixture]
    public class CloneUrlTests
    {
        // Azure DevOps' own form of a clone address, with the user name it offers left out (Issue 8 design, The URL)
        [TestCase("https://dev.azure.com/BlacklightInternalsMo", "Kudos Wall", "victim-repo", "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo")]
        [TestCase("https://dev.azure.com/BlacklightInternalsMo/", "Kudos Wall", "victim-repo", "https://dev.azure.com/BlacklightInternalsMo/Kudos%20Wall/_git/victim-repo")]
        public void For_BuildsTheAzureDevOpsCloneUrl(string organizationUrl, string projectName, string repositoryName, string expected)
        {
            Assert.That(CloneUrl.For(organizationUrl, projectName, repositoryName), Is.EqualTo(expected));
        }

        // Review Focus 1: a & or # left as it is would end the path, so git would ask for another repository
        [Test]
        public void For_EscapesEveryCharacterAUrlCannotHold()
        {
            Assert.That(CloneUrl.For("https://dev.azure.com/org", "R&D #1", "Café+api"), Is.EqualTo("https://dev.azure.com/org/R%26D%20%231/_git/Caf%C3%A9%2Bapi"));
        }
    }
}
