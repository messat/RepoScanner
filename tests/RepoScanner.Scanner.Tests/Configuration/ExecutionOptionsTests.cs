using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class ExecutionOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<ExecutionOptions> _validator = new DataAnnotationValidateOptions<ExecutionOptions>(null);

        // Issue 7 design, Settings: 50 stops before the job's 60-minute replicaTimeout, 15 is about 2.5 times
        // ADR-012's estimate per repository, and 65 is replicaTimeout plus 5 (ADR-013)
        [Test]
        public void Defaults_AreTheAgreedTimeLimits()
        {
            ExecutionOptions options = new ExecutionOptions();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(options.MaxRunDurationMinutes, Is.EqualTo(50));
                Assert.That(options.ScanTimeoutPerRepoMinutes, Is.EqualTo(15));
                Assert.That(options.VisibilityTimeoutMinutes, Is.EqualTo(65));
            }
        }

        [Test]
        public void Validate_WithTheDefaults_Succeeds()
        {
            ValidateOptionsResult result = _validator.Validate(null, new ExecutionOptions());

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [TestCase(0, 15, 65, nameof(ExecutionOptions.MaxRunDurationMinutes))]
        [TestCase(1441, 15, 65, nameof(ExecutionOptions.MaxRunDurationMinutes))]
        [TestCase(50, 0, 65, nameof(ExecutionOptions.ScanTimeoutPerRepoMinutes))]
        [TestCase(50, 15, 0, nameof(ExecutionOptions.VisibilityTimeoutMinutes))]
        [TestCase(50, 15, 10081, nameof(ExecutionOptions.VisibilityTimeoutMinutes))]
        public void Validate_WithAValueOutOfRange_FailsNamingIt(int maxRunDuration, int scanTimeoutPerRepo, int visibilityTimeout, string expectedSetting)
        {
            ExecutionOptions options = new ExecutionOptions
            {
                MaxRunDurationMinutes = maxRunDuration,
                ScanTimeoutPerRepoMinutes = scanTimeoutPerRepo,
                VisibilityTimeoutMinutes = visibilityTimeout
            };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(expectedSetting));
            }
        }

        // With a per-repository limit longer than the whole run, no repository could ever be started
        [Test]
        public void Validate_WithAScanTimeoutLongerThanTheRun_Fails()
        {
            ExecutionOptions options = new ExecutionOptions { MaxRunDurationMinutes = 10, ScanTimeoutPerRepoMinutes = 11 };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("ScanTimeoutPerRepoMinutes"));
            }
        }

        [Test]
        public void Validate_WithAScanTimeoutAsLongAsTheRun_Succeeds()
        {
            ExecutionOptions options = new ExecutionOptions { MaxRunDurationMinutes = 15, ScanTimeoutPerRepoMinutes = 15 };

            Assert.That(_validator.Validate(null, options).Succeeded, Is.True);
        }

        // Development's 1 minute, so a retry can be watched in Storage Explorer, is shorter than the per-repository
        // limit and is still valid
        [Test]
        public void Validate_WithAVisibilityTimeoutShorterThanTheScanTimeout_Succeeds()
        {
            ExecutionOptions options = new ExecutionOptions { VisibilityTimeoutMinutes = 1 };

            Assert.That(_validator.Validate(null, options).Succeeded, Is.True);
        }
    }
}
