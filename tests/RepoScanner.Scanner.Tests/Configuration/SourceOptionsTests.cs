using System.IO;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class SourceOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<SourceOptions> _validator = new DataAnnotationValidateOptions<SourceOptions>(null);

        [Test]
        public void Validate_WithAFolderThatExists_Succeeds()
        {
            using (TempFolder root = new TempFolder())
            {
                ValidateOptionsResult result = _validator.Validate(null, new SourceOptions { LocalRoot = root.FullPath });

                Assert.That(result.Succeeded, Is.True, result.FailureMessage);
            }
        }

        // The one log line says exactly what to set, and where
        [TestCase("")]
        [TestCase("   ")]
        public void Validate_WithoutLocalRoot_FailsSayingWhatToSet(string localRoot)
        {
            ValidateOptionsResult result = _validator.Validate(null, new SourceOptions { LocalRoot = localRoot });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Scanner:Source:LocalRoot").And.Contain("Scanner__Source__LocalRoot"));
            }
        }

        [Test]
        public void Validate_WithAFolderThatDoesNotExist_FailsNamingIt()
        {
            string missing = Path.Combine(Path.GetTempPath(), "reposcanner-tests", "no-such-folder");

            ValidateOptionsResult result = _validator.Validate(null, new SourceOptions { LocalRoot = missing });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Scanner:Source:LocalRoot").And.Contain(missing));
            }
        }
    }
}
