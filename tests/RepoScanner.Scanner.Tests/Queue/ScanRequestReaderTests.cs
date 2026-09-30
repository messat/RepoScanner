using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RepoScanner.Core.Persistence;
using RepoScanner.Scanner.Queue;

namespace RepoScanner.Scanner.Tests.Queue
{
    [TestFixture]
    public class ScanRequestReaderTests
    {
        // The example message in the Issue 7 design, one field per entry as raw JSON, so a test can
        // change or leave out one field and keep the rest valid
        private static readonly (string Name, string Json)[] ValidFields =
        {
            ("schemaVersion", "1"),
            ("correlationId", "\"5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c\""),
            ("repositoryId", "\"0f8fad5b-d9cb-469f-a165-70867728950e\""),
            ("projectName", "\"Lumina\""),
            ("repositoryName", "\"RepoScanner\""),
            ("defaultBranch", "\"develop\"")
        };

        [Test]
        public void TryRead_AValidMessage_ReturnsEveryField()
        {
            bool read = ScanRequestReader.TryRead(Message(), out ScanRequest? request, out string? problem);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.True);
                Assert.That(problem, Is.Null);
                Assert.That(request, Is.EqualTo(new ScanRequest(
                    new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c"),
                    new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
                    "Lumina",
                    "RepoScanner",
                    "develop")));
            }
        }

        [Test]
        public void TryRead_FieldNamesInAnotherCase_ReturnsTheRequest()
        {
            string body = "{\"SchemaVersion\": 1, \"CORRELATIONID\": \"5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c\", "
                + "\"RepositoryId\": \"0f8fad5b-d9cb-469f-a165-70867728950e\", \"ProjectName\": \"Lumina\", "
                + "\"repositoryname\": \"RepoScanner\", \"DefaultBranch\": \"develop\"}";

            bool read = ScanRequestReader.TryRead(body, out ScanRequest? request, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.True);
                Assert.That(request?.RepositoryName, Is.EqualTo("RepoScanner"));
            }
        }

        [Test]
        public void TryRead_WithAnExtraField_IgnoresIt()
        {
            string body = Build(ValidFields.Append(("requestedBy", "\"dashboard\"")));

            Assert.That(ScanRequestReader.TryRead(body, out _, out _), Is.True);
        }

        [TestCase("schemaVersion")]
        [TestCase("correlationId")]
        [TestCase("repositoryId")]
        [TestCase("projectName")]
        [TestCase("repositoryName")]
        [TestCase("defaultBranch")]
        public void TryRead_WithAFieldMissing_SaysWhichIsMissing(string name)
        {
            AssertMalformed(MessageWithout(name), name + " is missing");
        }

        [TestCase("schemaVersion", "\"1\"", "schemaVersion must be a whole number")]
        [TestCase("schemaVersion", "1.5", "schemaVersion must be a whole number")]
        [TestCase("correlationId", "42", "correlationId must be a GUID")]
        [TestCase("correlationId", "\"not-a-guid\"", "correlationId must be a GUID")]
        [TestCase("repositoryId", "null", "repositoryId must be a GUID")]
        [TestCase("projectName", "null", "projectName must be text")]
        [TestCase("repositoryName", "123", "repositoryName must be text")]
        [TestCase("defaultBranch", "true", "defaultBranch must be text")]
        public void TryRead_WithAFieldOfTheWrongType_SaysWhatItMustBe(string name, string json, string expectedProblem)
        {
            AssertMalformed(MessageWith(name, json), expectedProblem);
        }

        [TestCase("projectName", "\"\"")]
        [TestCase("repositoryName", "\"   \"")]
        [TestCase("defaultBranch", "\"\"")]
        public void TryRead_WithAnEmptyText_SaysItMustNotBeEmpty(string name, string json)
        {
            AssertMalformed(MessageWith(name, json), name + " must not be empty");
        }

        [TestCase("correlationId")]
        [TestCase("repositoryId")]
        public void TryRead_WithAnAllZeroGuid_SaysItMustNotBeAllZeros(string name)
        {
            AssertMalformed(MessageWith(name, "\"00000000-0000-0000-0000-000000000000\""), name + " must not be all zeros");
        }

        [Test]
        public void TryRead_WithSchemaVersionTwo_SaysOnlyVersionOneIsRead()
        {
            AssertMalformed(MessageWith("schemaVersion", "2"), "schemaVersion 2 is not supported: this scanner reads version 1");
        }

        [TestCase("")]
        [TestCase("hello")]
        [TestCase("{\"schemaVersion\": 1,")]
        public void TryRead_ABodyThatIsNotJson_SaysSo(string body)
        {
            AssertMalformed(body, "The message is not JSON");
        }

        [Test]
        public void TryRead_AValidMessageSentAsBase64_IsNotJson()
        {
            // What Storage Explorer sends when "Encode message body in Base64" is left ticked
            string body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Message()));

            AssertMalformed(body, "The message is not JSON");
        }

        [TestCase("[]")]
        [TestCase("1")]
        [TestCase("\"RepoScanner\"")]
        public void TryRead_JsonThatIsNotAnObject_SaysSo(string body)
        {
            AssertMalformed(body, "The message is not a JSON object");
        }

        [Test]
        public void TryRead_WithAFieldTwice_SaysItAppearsMoreThanOnce()
        {
            // Two readers could each take a different one of the two, so neither is trusted
            string body = Build(ValidFields.Append(("RepositoryId", "\"11111111-2222-3333-4444-555555555555\"")));

            AssertMalformed(body, "repositoryId appears more than once");
        }

        [TestCase("\".\"", "repositoryName must not be . or ..")]
        [TestCase("\"..\"", "repositoryName must not be . or ..")]
        [TestCase(@"""..\\..\\Windows""", @"repositoryName must not contain \ or /")]
        [TestCase("\"Lumina/RepoScanner\"", @"repositoryName must not contain \ or /")]
        [TestCase("\"Repo:Scanner\"", "repositoryName must not contain < > : \" | ? * or control characters")]
        [TestCase("\"Repo*\"", "repositoryName must not contain < > : \" | ? * or control characters")]
        [TestCase("\"Repo\\u0001Scanner\"", "repositoryName must not contain < > : \" | ? * or control characters")]
        public void TryRead_WithARepositoryNameThatIsNotAFolderName_SaysWhy(string json, string expectedProblem)
        {
            AssertMalformed(MessageWith("repositoryName", json), expectedProblem);
        }

        [TestCase("Repo.Scanner")]
        [TestCase("My Repo")]
        [TestCase("repo-1_final")]
        public void TryRead_WithAnOrdinaryRepositoryName_ReturnsIt(string name)
        {
            bool read = ScanRequestReader.TryRead(MessageWith("repositoryName", "\"" + name + "\""), out ScanRequest? request, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.True);
                Assert.That(request?.RepositoryName, Is.EqualTo(name));
            }
        }

        [TestCase("refs/heads/develop")]
        [TestCase("REFS/heads/main")]
        public void TryRead_WithADefaultBranchStartingRefs_SaysToSendTheBranchName(string branch)
        {
            AssertMalformed(MessageWith("defaultBranch", "\"" + branch + "\""), "defaultBranch must be a branch name such as develop, not refs/heads/develop");
        }

        [Test]
        public void TryRead_WithASlashInsideTheDefaultBranch_ReturnsIt()
        {
            bool read = ScanRequestReader.TryRead(MessageWith("defaultBranch", "\"release/2026\""), out ScanRequest? request, out _);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.True);
                Assert.That(request?.DefaultBranch, Is.EqualTo("release/2026"));
            }
        }

        // The reason goes into the "message poisoned" log line, so it must never repeat what was sent
        [TestCase("password=hunter2", "hunter2")]
        [TestCase(@"{""schemaVersion"": 1, ""correlationId"": ""hunter2""}", "hunter2")]
        public void TryRead_WhenMalformed_NeverRepeatsTheBody(string body, string sentValue)
        {
            ScanRequestReader.TryRead(body, out _, out string? problem);

            Assert.That(problem, Does.Not.Contain(sentValue));
        }

        [Test]
        public void TryRead_WithABadRepositoryName_NeverRepeatsIt()
        {
            ScanRequestReader.TryRead(MessageWith("repositoryName", @"""..\\secret-folder-name"""), out _, out string? problem);

            Assert.That(problem, Does.Not.Contain("secret-folder-name"));
        }

        [Test]
        public void ToRepositoryDetails_GivesTheDetailsStartRunAsyncSaves()
        {
            ScanRequest request = new ScanRequest(
                new Guid("5b1c6f3e-8a2d-4e7f-9c1a-2d3e4f5a6b7c"),
                new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
                "Lumina",
                "RepoScanner",
                "develop");

            Assert.That(request.ToRepositoryDetails(), Is.EqualTo(new RepositoryDetails(
                new Guid("0f8fad5b-d9cb-469f-a165-70867728950e"),
                "Lumina",
                "RepoScanner",
                "develop")));
        }

        private static void AssertMalformed(string body, string expectedProblem)
        {
            bool read = ScanRequestReader.TryRead(body, out ScanRequest? request, out string? problem);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.False);
                Assert.That(request, Is.Null);
                Assert.That(problem, Is.EqualTo(expectedProblem));
            }
        }

        private static string Message()
        {
            return Build(ValidFields);
        }

        private static string MessageWith(string name, string json)
        {
            return Build(ValidFields.Select(field => field.Name == name ? (name, json) : field));
        }

        private static string MessageWithout(string name)
        {
            return Build(ValidFields.Where(field => field.Name != name));
        }

        private static string Build(IEnumerable<(string Name, string Json)> fields)
        {
            return "{" + string.Join(", ", fields.Select(field => "\"" + field.Name + "\": " + field.Json)) + "}";
        }
    }
}
