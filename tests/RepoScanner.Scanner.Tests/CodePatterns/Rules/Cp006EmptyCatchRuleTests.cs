using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp006EmptyCatchRuleTests
    {
        private readonly Cp006EmptyCatchRule _rule = new Cp006EmptyCatchRule();

        // CA1031 is about catching general exceptions rather than empty blocks, so the reference is "related to"
        [Test]
        public void Rule_IsLowAndCitesCA1031AsRelated()
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, "try { Save(); } catch { }").Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP006"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.Low));
                Assert.That(match.Message, Does.StartWith("This catch block is empty")
                    .And.EndsWith("(related to CA1031: https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1031)"));
            }
        }

        // With or without an exception type, a variable or a filter. The match is the whole catch.
        [TestCase("try { Save(); } catch { }", "catch { }")]
        [TestCase("try { Save(); } catch (Exception) { }", "catch (Exception) { }")]
        [TestCase("try { Save(); } catch (IOException exception) when (exception.HResult == 32) { }",
            "catch (IOException exception) when (exception.HResult == 32) { }")]
        [TestCase("try { Save(); }\ncatch (Exception)\n{\n}", "catch (Exception)\n{\n}")]
        public void FindMatches_FlagsAnEmptyCatch(string statements, string expected)
        {
            Assert.That(PatternMatches.InMethod(_rule, statements), Is.EqualTo(new string[] { expected }));
        }

        // A comment inside the block records a deliberate choice (design decision 14)
        [TestCase("try { Save(); } catch { /* the file may already be gone */ }")]
        [TestCase("try { Save(); }\ncatch (IOException)\n{\n    // the file may already be gone\n}")]
        [TestCase("try { Save(); } catch { throw; }")]
        [TestCase("try { Save(); } catch (Exception exception) { logger.LogWarning(exception, \"Save failed\"); }")]
        [TestCase("try { Save(); } finally { }")]
        public void FindMatches_IgnoresACatchThatDoesSomethingOrSaysWhy(string statements)
        {
            Assert.That(PatternMatches.InMethod(_rule, statements), Is.Empty);
        }

        [Test]
        public void FindMatches_FlagsEachEmptyCatch()
        {
            IReadOnlyList<string> matches = PatternMatches.InMethod(_rule,
                "try { Save(); } catch (IOException) { } catch (Exception exception) { Log(exception); } try { Load(); } catch { }");

            Assert.That(matches, Is.EqualTo(new string[] { "catch (IOException) { }", "catch { }" }));
        }
    }
}
