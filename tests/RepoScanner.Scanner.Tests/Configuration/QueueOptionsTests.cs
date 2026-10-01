using System;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class QueueOptionsTests
    {
        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<QueueOptions> _validator = new DataAnnotationValidateOptions<QueueOptions>(null);

        // ADR-013: a message is poisoned after three failed attempts
        [Test]
        public void MaxDequeueCount_DefaultsToThree()
        {
            QueueOptions options = new QueueOptions();

            Assert.That(options.MaxDequeueCount, Is.EqualTo(3));
        }

        // ADR-013: each lane has its own poison queue
        [TestCase("scan-nightly", "scan-nightly-poison")]
        [TestCase("scan-ondemand", "scan-ondemand-poison")]
        public void PoisonQueueName_WhenNotSet_IsTheQueueNameWithPoisonSuffix(string name, string expectedPoisonQueueName)
        {
            QueueOptions options = new QueueOptions { Name = name };

            Assert.That(options.PoisonQueueName, Is.EqualTo(expectedPoisonQueueName));
        }

        [Test]
        public void PoisonQueueName_WhenSet_OverridesTheDefault()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", PoisonQueueName = "scan-custom-poison" };

            Assert.That(options.PoisonQueueName, Is.EqualTo("scan-custom-poison"));
        }

        // Azure queue names are at most 63 characters
        [Test]
        public void Validate_WhenPoisonQueueNameIsTooLong_Fails()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true", PoisonQueueName = new string('a', 64) };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain(nameof(QueueOptions.PoisonQueueName)));
        }

        // Issue 7 design, decision 5: the queue says which lane a scan came from, so only the two lanes are valid
        [TestCase("scan-weekly")]
        [TestCase("scan-test")]
        public void Validate_WithAQueueThatIsNotALane_FailsNamingBothLanes(string name)
        {
            QueueOptions options = new QueueOptions { Name = name, ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("scan-nightly").And.Contain("scan-ondemand"));
            }
        }

        [TestCase("scan-nightly", ScanTrigger.Nightly)]
        [TestCase("scan-ondemand", ScanTrigger.OnDemand)]
        public void Trigger_IsTheLaneTheQueueBelongsTo(string name, ScanTrigger expectedTrigger)
        {
            QueueOptions options = new QueueOptions { Name = name };

            Assert.That(options.Trigger, Is.EqualTo(expectedTrigger));
        }

        // Issue 7 design, decision 14: long enough to be noticed; Azure's default is 7 days
        [Test]
        public void PoisonMessageTimeToLiveDays_DefaultsToThirty()
        {
            QueueOptions options = new QueueOptions();

            Assert.That(options.PoisonMessageTimeToLiveDays, Is.EqualTo(30));
        }

        [TestCase(0)]
        [TestCase(366)]
        public void Validate_WithPoisonMessageTimeToLiveDaysOutOfRange_Fails(int days)
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true", PoisonMessageTimeToLiveDays = days };

            ValidateOptionsResult result = _validator.Validate(null, options);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(nameof(QueueOptions.PoisonMessageTimeToLiveDays)));
            }
        }

        // A missing Name is reported once, not again as an invalid poison queue name
        [Test]
        public void Validate_WithoutName_DoesNotAlsoReportPoisonQueueName()
        {
            QueueOptions options = new QueueOptions { ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.FailureMessage, Does.Not.Contain(nameof(QueueOptions.PoisonQueueName)));
        }

        [Test]
        public void Validate_WithNameAndConnectionString_Succeeds()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [Test]
        public void Validate_WithNameAndServiceUri_Succeeds()
        {
            QueueOptions options = new QueueOptions { Name = "scan-ondemand", ServiceUri = new Uri("https://example.queue.core.windows.net") };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        [Test]
        public void Validate_WithoutName_FailsNamingTheSetting()
        {
            QueueOptions options = new QueueOptions { ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain(nameof(QueueOptions.Name)));
        }

        [TestCase("Scan-Nightly")]
        [TestCase("scan_nightly")]
        [TestCase("ab")]
        public void Validate_WithInvalidQueueName_Fails(string name)
        {
            QueueOptions options = new QueueOptions { Name = name, ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
        }

        [Test]
        public void Validate_WithBothConnectionStringAndServiceUri_Fails()
        {
            QueueOptions options = new QueueOptions
            {
                Name = "scan-nightly",
                ConnectionString = "UseDevelopmentStorage=true",
                ServiceUri = new Uri("https://example.queue.core.windows.net")
            };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain("Exactly one"));
        }

        [Test]
        public void Validate_WithNeitherConnectionStringNorServiceUri_Fails()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain("Exactly one"));
        }

        [Test]
        public void Validate_WithMaxDequeueCountZero_Fails()
        {
            QueueOptions options = new QueueOptions { Name = "scan-nightly", ConnectionString = "UseDevelopmentStorage=true", MaxDequeueCount = 0 };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
        }
    }
}
