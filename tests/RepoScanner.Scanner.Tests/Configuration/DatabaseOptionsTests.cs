using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class DatabaseOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<DatabaseOptions> _validator = new DataAnnotationValidateOptions<DatabaseOptions>(null);

        // #71: the message names the setting in full, so the fix is obvious from one log line
        [TestCase("")]
        [TestCase("   ")]
        public void Validate_WithoutConnectionString_FailsNamingTheSetting(string connectionString)
        {
            DatabaseOptions options = new DatabaseOptions { ConnectionString = connectionString };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Scanner:Database:ConnectionString"));
            }
        }

        [Test]
        public void Validate_WithConnectionString_Succeeds()
        {
            DatabaseOptions options = new DatabaseOptions { ConnectionString = "Server=.\\SQLEXPRESS;Database=RepoScanner;Trusted_Connection=True" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }
    }
}
