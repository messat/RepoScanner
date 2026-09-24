using System.Reflection;
using NUnit.Framework;

namespace RepoScanner.Core.Tests
{
    // Smoke test: proves this test project references and can load RepoScanner.Core.
    // Replaced by real tests once Core has types (Issue 2 onwards).
    [TestFixture]
    public class CoreAssemblyTests
    {
        [Test]
        public void CoreAssembly_WhenLoadedByName_IsFound()
        {
            Assembly assembly = Assembly.Load("RepoScanner.Core");

            Assert.That(assembly.GetName().Name, Is.EqualTo("RepoScanner.Core"));
        }
    }
}
