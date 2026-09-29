using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using RepoScanner.Scanner.CodePatterns;

namespace RepoScanner.Scanner.Tests.CodePatterns
{
    // The recipe in ADR-024's Issue 6 amendment: SHA-256 of "<type>|<member>|<tokens>". Each test finds the
    // call to Flag() in the code and checks its hash against the parts written out by hand.
    [TestFixture]
    public class SnippetHashTests
    {
        [TestCase("class Orders { void Save() { Flag(); } }", "Orders|Save|Flag ( )")]
        [TestCase("class Outer { class Inner { void Run() { Flag(); } } }", "Outer.Inner|Run|Flag ( )")]
        [TestCase("namespace Shop { struct Money { void Add() { Flag(); } } }", "Money|Add|Flag ( )")]
        [TestCase("record Order { void Save() { Flag(); } }", "Order|Save|Flag ( )")]
        [TestCase("class Orders { Orders() { Flag(); } }", "Orders|Orders|Flag ( )")]
        [TestCase("class Orders { int Total { get { Flag(); return 0; } } }", "Orders|Total|Flag ( )")]
        [TestCase("class Orders { int Total => Flag(); }", "Orders|Total|Flag ( )")]
        [TestCase("class Orders { int this[int index] { get { Flag(); return index; } } }", "Orders|this|Flag ( )")]
        [TestCase("class Orders { event Action Saved { add { Flag(); } remove { } } }", "Orders|Saved|Flag ( )")]
        [TestCase("class Orders { event Action Saved = () => Flag(); }", "Orders|Saved|Flag ( )")]
        [TestCase("class Orders { void Save() { Action later = () => Flag(); } }", "Orders|Save|Flag ( )")]
        [TestCase("class Orders { void Save() { void Local() { Flag(); } } }", "Orders|Save|Flag ( )")]
        [TestCase("Flag();", "||Flag ( )")]
        public void Compute_HashesTheTypeTheMemberAndTheTokens(string code, string identity)
        {
            Assert.That(SnippetHash.Compute(FlagCall(code)), Is.EqualTo(Sha256Hex(identity)));
        }

        // The field the code sits in, not the first one declared with it
        [Test]
        public void Compute_InAFieldInitialiser_UsesThatFieldsName()
        {
            string code = "class Orders { private readonly Action _first = null, _save = () => { Action inner = () => Flag(); }; }";

            Assert.That(SnippetHash.Compute(FlagCall(code)), Is.EqualTo(Sha256Hex("Orders|_save|Flag ( )")));
        }

        // Reformatting a file, adding lines above the code, or commenting inside it keeps the fingerprint,
        // so a suppression stays with the finding
        [Test]
        public void Compute_IgnoresSpacingLineBreaksAndComments()
        {
            string code = "\n\n\nclass Orders\n{\n    // Saves the order\n    void Save()\n    {\n        Flag (  /* why */\n        );\n    }\n}\n";

            Assert.That(SnippetHash.Compute(FlagCall(code)), Is.EqualTo(Sha256Hex("Orders|Save|Flag ( )")));
        }

        // A string over several lines holds its line breaks, and git on Windows checks files out with CRLF
        // endings, so the same code must give the same hash on Windows and in the Linux container
        [Test]
        public void Compute_CountsWindowsLineEndingsInsideAStringAsLineFeeds()
        {
            string code = "class Orders { void Save() { Flag(@\"first\r\nsecond\"); } }";

            Assert.That(SnippetHash.Compute(FlagCall(code)), Is.EqualTo(Sha256Hex("Orders|Save|Flag ( @\"first\nsecond\" )")));
        }

        private static InvocationExpressionSyntax FlagCall(string code)
        {
            SyntaxNode root = CSharpSyntaxTree.ParseText(code).GetRoot();

            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Single(call => call.Expression is IdentifierNameSyntax name && name.Identifier.ValueText == "Flag");
        }

        private static string Sha256Hex(string identity)
        {
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        }
    }
}
