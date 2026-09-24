using System;
using Microsoft.Extensions.Options;
using NUnit.Framework;
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

        // A 60-character queue name is valid, but its default poison queue name (67 characters) is not
        [Test]
        public void Validate_WhenDefaultPoisonQueueNameIsTooLong_Fails()
        {
            QueueOptions options = new QueueOptions { Name = new string('a', 60), ConnectionString = "UseDevelopmentStorage=true" };

            ValidateOptionsResult result = _validator.Validate(null, options);

            Assert.That(result.Failed, Is.True);
            Assert.That(result.FailureMessage, Does.Contain(nameof(QueueOptions.PoisonQueueName)));
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
