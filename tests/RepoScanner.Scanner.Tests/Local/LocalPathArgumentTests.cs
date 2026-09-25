using System;
using NUnit.Framework;
using RepoScanner.Scanner.Local;

namespace RepoScanner.Scanner.Tests.Local
{
    [TestFixture]
    public class LocalPathArgumentTests
    {
        // Each case is one command line. A string[] cannot be an attribute argument (CS0182), so the
        // cases are fields read by TestCaseSource.
        private static readonly object[] ArgumentsWithAFolder =
        {
            new object[] { new string[] { "--path", "src" } },
            new object[] { new string[] { "--path=src" } },
            new object[] { new string[] { "--PATH", "src" } },
            new object[] { new string[] { "--Scanner:Database:ConnectionString", "x", "--path", "src" } }
        };

        // --path is there but names no folder: an argument starting "--" is another setting
        private static readonly object[] ArgumentsWithoutAFolder =
        {
            new object[] { new string[] { "--path" } },
            new object[] { new string[] { "--path=" } },
            new object[] { new string[] { "--path", "--Scanner:Queue:Name", "scan-test" } }
        };

        private static readonly object[] ArgumentsWithoutPath =
        {
            new object[] { new string[] { "--Scanner:Queue:Name", "scan-test" } },
            new object[] { new string[] { "--pathway", "src" } },
            new object[] { Array.Empty<string>() }
        };

        [TestCaseSource(nameof(ArgumentsWithAFolder))]
        public void TryGet_WithAFolder_ReturnsIt(string[] args)
        {
            bool found = LocalPathArgument.TryGet(args, out string? folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(folder, Is.EqualTo("src"));
            }
        }

        [TestCaseSource(nameof(ArgumentsWithoutAFolder))]
        public void TryGet_WithoutAFolder_ReturnsTrueAndNull(string[] args)
        {
            bool found = LocalPathArgument.TryGet(args, out string? folder);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(found, Is.True);
                Assert.That(folder, Is.Null);
            }
        }

        [TestCaseSource(nameof(ArgumentsWithoutPath))]
        public void TryGet_WithoutPath_ReturnsFalse(string[] args)
        {
            Assert.That(LocalPathArgument.TryGet(args, out _), Is.False);
        }
    }
}
