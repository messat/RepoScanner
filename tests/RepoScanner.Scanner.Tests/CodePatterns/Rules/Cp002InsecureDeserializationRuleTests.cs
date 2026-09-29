using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RepoScanner.Core.Enums;
using RepoScanner.Scanner.CodePatterns;
using RepoScanner.Scanner.CodePatterns.Rules;

namespace RepoScanner.Scanner.Tests.CodePatterns.Rules
{
    [TestFixture]
    public class Cp002InsecureDeserializationRuleTests
    {
        private const string LearnPages = "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/";

        private readonly Cp002InsecureDeserializationRule _rule = new Cp002InsecureDeserializationRule();

        [Test]
        public void Rule_IsHigh()
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_rule.RuleId, Is.EqualTo("CP002"));
                Assert.That(_rule.Severity, Is.EqualTo(Severity.High));
            }
        }

        // Each formatter Microsoft marks insecure, however its type is written, with its own analyser ID
        [TestCase("BinaryFormatter formatter = new BinaryFormatter();", "new BinaryFormatter()", "BinaryFormatter", "CA2300")]
        [TestCase("object value = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Deserialize(stream);",
            "new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter()", "BinaryFormatter", "CA2300")]
        [TestCase("LosFormatter formatter = new LosFormatter();", "new LosFormatter()", "LosFormatter", "CA2305")]
        [TestCase("NetDataContractSerializer serializer = new NetDataContractSerializer();", "new NetDataContractSerializer()", "NetDataContractSerializer", "CA2310")]
        [TestCase("ObjectStateFormatter formatter = new ObjectStateFormatter();", "new ObjectStateFormatter()", "ObjectStateFormatter", "CA2315")]
        public void FindMatches_FlagsAnInsecureFormatter(string statement, string expectedNode, string type, string analyserId)
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, statement).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(match.Node.ToString(), Is.EqualTo(expectedNode));
                Assert.That(match.Message, Does.StartWith(type + " can run an attacker's code")
                    .And.EndsWith("(" + analyserId + ": " + LearnPages + analyserId.ToLowerInvariant() + ")"));
            }
        }

        // Newtonsoft.Json's TypeNameHandling other than None, wherever it is written. The property name on
        // the left of TypeNameHandling = TypeNameHandling.Auto is not a match, so the setting is one finding.
        [TestCase("JsonSerializerSettings settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto };", "TypeNameHandling.Auto")]
        [TestCase("settings.TypeNameHandling = TypeNameHandling.All;", "TypeNameHandling.All")]
        [TestCase("settings.TypeNameHandling = TypeNameHandling.Objects;", "TypeNameHandling.Objects")]
        [TestCase("settings.TypeNameHandling = Newtonsoft.Json.TypeNameHandling.Arrays;", "Newtonsoft.Json.TypeNameHandling.Arrays")]
        public void FindMatches_FlagsTypeNameHandlingOtherThanNone(string statement, string expectedNode)
        {
            CodePatternMatch match = PatternMatches.FoundInMethod(_rule, statement).Single();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(match.Node.ToString(), Is.EqualTo(expectedNode));
                Assert.That(match.Message, Does.StartWith("TypeNameHandling other than None")
                    .And.EndsWith("(CA2326: " + LearnPages + "ca2326)"));
            }
        }

        [Test]
        public void FindMatches_FlagsTypeNameHandlingInAnAttribute()
        {
            string code = "class Message { [JsonProperty(TypeNameHandling = TypeNameHandling.Auto)] public object Payload { get; set; } }";

            Assert.That(PatternMatches.In(_rule, code), Is.EqualTo(new string[] { "TypeNameHandling.Auto" }));
        }

        // TypeNameHandling.None is the safe setting, SoapFormatter has no analyser ID (design decision 13), and
        // reading the setting is not setting it
        [TestCase("settings.TypeNameHandling = TypeNameHandling.None;")]
        [TestCase("string text = settings.TypeNameHandling.ToString();")]
        [TestCase("TypeNameHandling handling = settings.TypeNameHandling;")]
        [TestCase("string json = JsonSerializer.Serialize(order);")]
        [TestCase("DataContractSerializer serializer = new DataContractSerializer(typeof(Order));")]
        [TestCase("SoapFormatter formatter = new SoapFormatter();")]
        [TestCase("BinaryReader reader = new BinaryReader(stream);")]
        public void FindMatches_IgnoresSafeSerialization(string statement)
        {
            Assert.That(PatternMatches.InMethod(_rule, statement), Is.Empty);
        }

        [Test]
        public void FindMatches_FlagsEachUse()
        {
            IReadOnlyList<string> matches = PatternMatches.InMethod(_rule,
                "BinaryFormatter formatter = new BinaryFormatter(); settings.TypeNameHandling = TypeNameHandling.All;");

            Assert.That(matches, Is.EqualTo(new string[] { "new BinaryFormatter()", "TypeNameHandling.All" }));
        }
    }
}
