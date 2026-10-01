using System;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NUnit.Framework;
using RepoScanner.Scanner.Configuration;

namespace RepoScanner.Scanner.Tests.Configuration
{
    [TestFixture]
    public class AzureDevOpsOptionsTests
    {
        // A fake PAT: values starting "your-" are placeholders to RepoScanner's own self-scan (Issue 3 design)
        private const string Pat = "your-test-pat-0001";

        private const string BadOrganizationUrl =
            "Scanner:AzureDevOps:OrganizationUrl must be an https URL such as https://dev.azure.com/<organisation>, with no user name in it";

        // The same validator that ValidateDataAnnotations() registers in Program
        private readonly DataAnnotationValidateOptions<AzureDevOpsOptions> _validator = new DataAnnotationValidateOptions<AzureDevOpsOptions>(null);

        // file: is how the tests point at bare repositories on disk (Issue 8 design, Testing)
        [TestCase("https://dev.azure.com/BlacklightInternalsMo")]
        [TestCase("https://dev.azure.com/BlacklightInternalsMo/")]
        [TestCase("file:///C:/repos")]
        public void Validate_WithAnHttpsOrFileUrlAndAPat_Succeeds(string organizationUrl)
        {
            ValidateOptionsResult result = _validator.Validate(null, new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat });

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        // The one log line says exactly what to set, and where
        [TestCase("")]
        [TestCase("   ")]
        public void Validate_WithoutAPat_SaysWhatToSet(string pat)
        {
            ValidateOptionsResult result = _validator.Validate(
                null, new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = pat });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain("Scanner:AzureDevOps:Pat").And.Contain("Scanner__AzureDevOps__Pat"));
            }
        }

        [TestCase("")]
        [TestCase("dev.azure.com/BlacklightInternalsMo")]
        [TestCase("http://dev.azure.com/BlacklightInternalsMo")]
        [TestCase("ftp://dev.azure.com/BlacklightInternalsMo")]
        // The address Azure DevOps' Clone dialog offers has the user name in it, which is the pattern this tool flags
        [TestCase("https://BlacklightInternalsMo@dev.azure.com/BlacklightInternalsMo")]
        public void Validate_WithABadOrganizationUrl_FailsWithoutShowingThePat(string organizationUrl)
        {
            ValidateOptionsResult result = _validator.Validate(null, new AzureDevOpsOptions { OrganizationUrl = organizationUrl, Pat = Pat });
            bool leaked = result.FailureMessage?.Contains(Pat, StringComparison.Ordinal) == true;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(BadOrganizationUrl));
                Assert.That(leaked, Is.False, "the PAT is in the failure message");
            }
        }

        // ADR-016's eight-minute cap on the clone (#40)
        [Test]
        public void CloneTimeoutMinutes_DefaultsToEight()
        {
            Assert.That(new AzureDevOpsOptions().CloneTimeoutMinutes, Is.EqualTo(8));
        }

        [TestCase(0)]
        [TestCase(1441)]
        public void Validate_CloneTimeoutOutsideOneTo1440_Fails(int minutes)
        {
            ValidateOptionsResult result = _validator.Validate(
                null, new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = Pat, CloneTimeoutMinutes = minutes });

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.Failed, Is.True);
                Assert.That(result.FailureMessage, Does.Contain(nameof(AzureDevOpsOptions.CloneTimeoutMinutes)));
            }
        }

        [TestCase(1)]
        [TestCase(1440)]
        public void Validate_CloneTimeoutInsideOneTo1440_Succeeds(int minutes)
        {
            ValidateOptionsResult result = _validator.Validate(
                null, new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = Pat, CloneTimeoutMinutes = minutes });

            Assert.That(result.Succeeded, Is.True, result.FailureMessage);
        }

        // A C# record's ToString prints every property, so a settings object in a log line would print the PAT
        [Test]
        public void ToString_HidesThePat()
        {
            string text = new AzureDevOpsOptions { OrganizationUrl = "https://dev.azure.com/BlacklightInternalsMo", Pat = Pat }.ToString();
            bool leaked = text.Contains(Pat, StringComparison.Ordinal);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(text, Does.Contain("OrganizationUrl = https://dev.azure.com/BlacklightInternalsMo"));
                Assert.That(text, Does.Contain("Pat = ***"));
                Assert.That(leaked, Is.False, "ToString printed the PAT");
            }
        }

        // Decision 5: locally the scanner clones from BlacklightInternalsMo, where victim-repo is. The organisation is not
        // a secret, so it is in appsettings.Development.json; the PAT is not.
        [Test]
        public void CreateBuilder_InDevelopment_ClonesFromBlacklightInternalsMo()
        {
            string[] args =
            {
                "--environment", "Development",
                "--Scanner:Queue:Name", "scan-nightly",
                "--Scanner:Queue:ConnectionString", "UseDevelopmentStorage=true",
                "--Scanner:Database:ConnectionString", "Server=unused;Database=unused",
                "--Scanner:AzureDevOps:Pat", Pat
            };

            using (IHost host = Program.CreateBuilder(args).Build())
            {
                AzureDevOpsOptions options = host.Services.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;

                Assert.That(options.OrganizationUrl, Is.EqualTo("https://dev.azure.com/BlacklightInternalsMo"));
            }
        }

        // ADR-007: no appsettings file ever holds the PAT
        [Test]
        public void AppSettingsFiles_HaveNoPat()
        {
            string[] files = Directory.GetFiles(Path.Combine(RepositoryRoot.FullPath, "src", "RepoScanner.Scanner"), "appsettings*.json");
            Assert.That(files, Is.Not.Empty);

            foreach (string file in files)
            {
                IConfiguration configuration = new ConfigurationBuilder().AddJsonFile(file).Build();

                Assert.That(configuration.GetSection(AzureDevOpsOptions.SectionName + ":Pat").Exists(), Is.False, Path.GetFileName(file));
            }
        }
    }
}
