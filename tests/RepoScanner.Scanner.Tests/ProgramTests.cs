using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace RepoScanner.Scanner.Tests
{
    [TestFixture]
    public class ProgramTests
    {
        // Runs the real host end to end. Exit codes for failures (1) and invalid config (2) arrive in task 5.
        [Test]
        public async Task Main_WithNoArguments_ReturnsZero()
        {
            int exitCode = await Program.Main(Array.Empty<string>());

            Assert.That(exitCode, Is.EqualTo(0));
        }
    }
}
