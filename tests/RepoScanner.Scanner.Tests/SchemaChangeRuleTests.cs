using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;
using RepoScanner.Data;

namespace RepoScanner.Scanner.Tests
{
    // ADR-006 and ADR-008: the scanner never creates, migrates or deletes the database. Migrations are
    // applied by dotnet ef locally and by the pipeline in Azure (ADR-017). This test reads the compiled
    // Scanner and Data assemblies, so it checks what will actually run: comments and strings cannot
    // cause a false alarm, and formatting cannot hide a call. Test projects are not checked, because
    // the SQLite tests build their schema with EnsureCreated.
    [TestFixture]
    public class SchemaChangeRuleTests
    {
        // Each EF Core type (namespace and name) with the methods on it that change the schema
        private static readonly Dictionary<string, string[]> SchemaChangingMethods = new Dictionary<string, string[]>
        {
            ["Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"] = new string[] { "Migrate", "MigrateAsync" },
            ["Microsoft.EntityFrameworkCore.Migrations.IMigrator"] = new string[] { "Migrate", "MigrateAsync" },
            ["Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade"] =
                new string[] { "EnsureCreated", "EnsureCreatedAsync", "EnsureDeleted", "EnsureDeletedAsync" }
        };

        [Test]
        public void ScannerAndData_NeverCallMethodsThatChangeTheSchema()
        {
            List<string> calls = new List<string>();
            calls.AddRange(SchemaChangingCallsIn(typeof(Program).Assembly));
            calls.AddRange(SchemaChangingCallsIn(typeof(ScannerDbContext).Assembly));

            Assert.That(calls, Is.Empty,
                "The scanner must never create, migrate or delete the database (ADR-006, ADR-008). " +
                "Apply migrations with dotnet ef locally or with the pipeline in Azure (README: Database and migrations).");
        }

        // Every method an assembly calls in another assembly is listed in its member reference table:
        // one entry per method, naming the method and the type it belongs to. A call in the code always
        // points at one of these entries, however it was written.
        private static List<string> SchemaChangingCallsIn(Assembly assembly)
        {
            List<string> calls = new List<string>();

            using (FileStream stream = File.OpenRead(assembly.Location))
            using (PEReader portableExecutable = new PEReader(stream))
            {
                MetadataReader metadata = portableExecutable.GetMetadataReader();

                foreach (MemberReferenceHandle handle in metadata.MemberReferences)
                {
                    MemberReference member = metadata.GetMemberReference(handle);

                    // The methods we look for belong to ordinary (non-generic) EF Core types
                    if (member.Parent.Kind != HandleKind.TypeReference)
                    {
                        continue;
                    }

                    TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
                    string typeName = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
                    string methodName = metadata.GetString(member.Name);

                    if (SchemaChangingMethods.TryGetValue(typeName, out string[]? methods) && Array.IndexOf(methods, methodName) >= 0)
                    {
                        calls.Add($"{assembly.GetName().Name} calls {typeName}.{methodName}");
                    }
                }
            }

            return calls;
        }
    }
}
