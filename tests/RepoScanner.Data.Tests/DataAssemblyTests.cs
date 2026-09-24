using System.Reflection;
using NUnit.Framework;

namespace RepoScanner.Data.Tests
{
    // Smoke test: proves this test project references and can load RepoScanner.Data.
    // Replaced by real repository tests in Issue 2.
    [TestFixture]
    public class DataAssemblyTests
    {
        [Test]
        public void DataAssembly_WhenLoadedByName_IsFound()
        {
            Assembly assembly = Assembly.Load("RepoScanner.Data");

            Assert.That(assembly.GetName().Name, Is.EqualTo("RepoScanner.Data"));
        }
    }
}
