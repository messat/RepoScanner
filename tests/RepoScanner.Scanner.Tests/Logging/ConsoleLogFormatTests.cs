using NUnit.Framework;
using RepoScanner.Scanner.Logging;

namespace RepoScanner.Scanner.Tests.Logging
{
    // Issue 10 design, decisions 9 and 10: text for a person at a terminal, JSON for Log Analytics, decided the same way
    // for the start-up logger and the host's
    [TestFixture]
    public class ConsoleLogFormatTests
    {
        [TestCase(null, "Development", false)]
        [TestCase(null, "Production", true)]
        [TestCase(null, "Staging", true)]
        [TestCase("Text", "Production", false)]
        [TestCase("Json", "Development", true)]
        // .NET settings usually ignore case, so json and TEXT are taken as Json and Text
        [TestCase("json", "Development", true)]
        [TestCase("TEXT", "Production", false)]
        // LoggingOptions rejects any other value (exit 2); until then, the environment's default
        [TestCase("Xml", "Production", true)]
        [TestCase("Xml", "Development", false)]
        public void IsJson_ChoosesBySettingThenEnvironment(string? setting, string environment, bool expected)
        {
            Assert.That(ConsoleLogFormat.IsJson(setting, environment), Is.EqualTo(expected));
        }

        // The start-up logger exists before configuration is read, so it reads the command line itself
        [TestCase(new[] { "--environment", "Development" }, false)]
        [TestCase(new[] { "--environment", "Production" }, true)]
        [TestCase(new[] { "--environment", "Development", "--Scanner:Logging:Format", "Json" }, true)]
        [TestCase(new[] { "--environment", "Production", "--Scanner:Logging:Format", "text" }, false)]
        public void IsJsonBeforeTheHost_ReadsTheCommandLine(string[] args, bool expected)
        {
            Assert.That(ConsoleLogFormat.IsJsonBeforeTheHost(args), Is.EqualTo(expected));
        }
    }
}
