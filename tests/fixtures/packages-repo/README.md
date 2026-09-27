# packages-repo fixture

A small, pretend repository for RepoScanner's package scanner tests and demo. The projects are never built, and some pin old package versions with known vulnerabilities on purpose.

| Folder | Shows |
|---|---|
| `plain/` | An SDK-style project with versions on each `<PackageReference>`, a floating version and a version written as an MSBuild property |
| `legacy/Plugins/` | An old-style .NET Framework project, like a Dynamics 365 plug-in: the MSBuild 2003 namespace, a `<Version>` child element and a floating version |
| `central/` | Central Package Management: `Directory.Packages.props`, two projects that use it, and a `VersionOverride` |
| `broken/` | A project file that is not valid XML |

Scan it with:

    dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/packages-repo

The `.reposcanner-exclude` file stops these packages being reported when the whole RepoScanner repository is scanned.
What the reader must find is in `tests/RepoScanner.Scanner.Tests/Packages/FixtureProjectFilesTests.cs`.
