using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Core.Scanning;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;
using RepoScanner.Scanner.Files;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp001SqlFromStringsRuleTests
    {
        private readonly Cp001SqlFromStringsRule _rule = new Cp001SqlFromStringsRule();

        [Test]
        public void Rule_IsHighAndCitesCA2100()
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText("class R { void M() { db.Users.FromSqlRaw(\"SELECT \" + name); } }").GetRoot();

            CodePatternMatch match = _rule.FindMatches(root).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP001"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
                Assert.That(match.Message, Does.Contain("SQL injection")
                    .And.EndsWith("(CA2100: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2100)"));
            }
        }

        // Each place SQL is given, with each way of building it from strings. The match is the whole call,
        // creation or assignment, which is what the finding's line and snippet hash come from.
        [TestCase("db.Users.FromSqlRaw(\"SELECT * FROM Users WHERE Name = '\" + name + \"'\");",
            "db.Users.FromSqlRaw(\"SELECT * FROM Users WHERE Name = '\" + name + \"'\")")]
        [TestCase("db.Users.FromSqlRaw($\"SELECT * FROM Users WHERE Name = '{name}'\");",
            "db.Users.FromSqlRaw($\"SELECT * FROM Users WHERE Name = '{name}'\")")]
        [TestCase("db.Users.FromSqlRaw(string.Format(\"SELECT * FROM Users WHERE Name = '{0}'\", name));",
            "db.Users.FromSqlRaw(string.Format(\"SELECT * FROM Users WHERE Name = '{0}'\", name))")]
        [TestCase("db.Users.FromSqlRaw(String.Concat(\"SELECT * FROM Users WHERE Name = '\", name, \"'\"));",
            "db.Users.FromSqlRaw(String.Concat(\"SELECT * FROM Users WHERE Name = '\", name, \"'\"))")]
        [TestCase("db.Users.FromSqlRaw(System.String.Format(\"SELECT {0}\", column));",
            "db.Users.FromSqlRaw(System.String.Format(\"SELECT {0}\", column))")]
        [TestCase("db.Users.FromSqlRaw((\"SELECT * FROM Users WHERE Id = \" + id));",
            "db.Users.FromSqlRaw((\"SELECT * FROM Users WHERE Id = \" + id))")]
        [TestCase("db.Users.FromSqlRaw(\"SELECT * \" + (\"FROM Users WHERE Id = \" + id));",
            "db.Users.FromSqlRaw(\"SELECT * \" + (\"FROM Users WHERE Id = \" + id))")]
        [TestCase("db.Database.ExecuteSqlRaw(\"DELETE FROM Users WHERE Id = \" + id);",
            "db.Database.ExecuteSqlRaw(\"DELETE FROM Users WHERE Id = \" + id)")]
        [TestCase("await db.Database.ExecuteSqlRawAsync($\"DELETE FROM Users WHERE Id = {id}\");",
            "db.Database.ExecuteSqlRawAsync($\"DELETE FROM Users WHERE Id = {id}\")")]
        [TestCase("db.Database.SqlQueryRaw<int>(\"SELECT Id FROM Users WHERE Name = '\" + name + \"'\");",
            "db.Database.SqlQueryRaw<int>(\"SELECT Id FROM Users WHERE Name = '\" + name + \"'\")")]
        [TestCase("SqlCommand command = new SqlCommand(\"SELECT * FROM Users WHERE Name = '\" + name + \"'\", connection);",
            "new SqlCommand(\"SELECT * FROM Users WHERE Name = '\" + name + \"'\", connection)")]
        [TestCase("SqlCommand command = new Microsoft.Data.SqlClient.SqlCommand($\"SELECT * FROM {table}\");",
            "new Microsoft.Data.SqlClient.SqlCommand($\"SELECT * FROM {table}\")")]
        [TestCase("command.CommandText = \"SELECT * FROM Users WHERE Name = '\" + name + \"'\";",
            "command.CommandText = \"SELECT * FROM Users WHERE Name = '\" + name + \"'\"")]
        [TestCase("SqlCommand command = new SqlCommand { Connection = connection, CommandText = $\"SELECT * FROM {table}\" };",
            "CommandText = $\"SELECT * FROM {table}\"")]
        public void FindMatches_FlagsSqlBuiltFromStrings(string statement, string expected)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.EqualTo(new string[] { expected }));
        }

        // FromSql, ExecuteSql and their Interpolated forms turn each hole into a parameter. Literals are safe
        // however they are joined, and SQL passed in a variable is not traced (design decision 12).
        [TestCase("db.Users.FromSql($\"SELECT * FROM Users WHERE Name = {name}\");")]
        [TestCase("db.Users.FromSqlInterpolated($\"SELECT * FROM Users WHERE Name = {name}\");")]
        [TestCase("db.Database.ExecuteSql($\"DELETE FROM Users WHERE Id = {id}\");")]
        [TestCase("db.Database.ExecuteSqlInterpolated($\"DELETE FROM Users WHERE Id = {id}\");")]
        [TestCase("db.Users.FromSqlRaw(\"SELECT * FROM Users WHERE Name = {0}\", name);")]
        [TestCase("db.Users.FromSqlRaw(\"SELECT * FROM Users \" + \"WHERE Active = \" + 1);")]
        [TestCase("db.Users.FromSqlRaw($\"SELECT * FROM Users\");")]
        [TestCase("db.Users.FromSqlRaw(sql);")]
        [TestCase("SqlCommand command = new SqlCommand(\"SELECT * FROM Users WHERE Name = @name\", connection);")]
        [TestCase("SqlCommand command = new SqlCommand();")]
        [TestCase("command.CommandText = \"SELECT 1\";")]
        [TestCase("label.Text = \"Hello \" + name;")]
        [TestCase("logger.Info(\"SELECT \" + name);")]
        [TestCase("string message = string.Format(\"Found {0} users\", count);")]
        public void FindMatches_IgnoresSafeSql(string statement)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.Empty);
        }

        // Whether a join holds only literals is checked before the walker goes below the call, so a check that
        // called itself for each + would overflow the stack, and end the process, on a long enough join. The
        // walker then stops with InsufficientExecutionStackException, and the scanner skips the file.
        [Test]
        public async Task ScanAsync_OnTwentyThousandJoinedLiterals_DoesNotCrash()
        {
            using (TempFolder folder = new TempFolder())
            {
                string joined = string.Join(" + ", Enumerable.Repeat("\"a\"", 20000));
                folder.WriteText("Long.cs", "class Long { void M() { db.Users.FromSqlRaw(" + joined + "); } }");
                CodePatternScanner scanner = new CodePatternScanner(
                    new RepositoryFileWalker(NullLogger<RepositoryFileWalker>.Instance), new ICodePatternRule[] { _rule }, NullLogger<CodePatternScanner>.Instance);

                IReadOnlyList<RawFinding> findings = await scanner.ScanAsync(folder.FullPath, CancellationToken.None);

                Assert.That(findings, Is.Empty);
            }
        }

        [Test]
        public void FindMatches_FlagsEachCall()
        {
            IReadOnlyList<string> matches = PatternMatches.InMethod(_rule,
                "db.Users.FromSqlRaw(\"SELECT * FROM Users WHERE Id = \" + id); db.Database.ExecuteSqlRaw($\"DELETE FROM Users WHERE Id = {id}\");");

            Assert.That(matches, Has.Count.EqualTo(2));
        }
    }
}
