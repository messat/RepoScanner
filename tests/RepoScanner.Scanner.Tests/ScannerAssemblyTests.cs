using System.Reflection;
using NUnit.Framework;

namespace RepoScanner.Scanner.Tests
{
    // Smoke test: proves this test project references RepoScanner.Scanner and can see Program.
    // Replaced by real tests from task 4 onwards (host, options, exit codes).
    [TestFixture]
    public class ScannerAssemblyTests
    {
        [Test]
        public void ScannerAssembly_ContainsProgramEntryPoint()
        {
            Assembly assembly = typeof(Program).Assembly;

            Assert.That(assembly.EntryPoint?.DeclaringType, Is.EqualTo(typeof(Program)));
        }
    }
}
