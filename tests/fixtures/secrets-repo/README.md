# secrets-repo fixture

A small, pretend repository for RepoScanner's tests and demo. Every secret in it is fake and has never worked anywhere.

Scan it with:

    dotnet run --project src/RepoScanner.Scanner -- --path tests/fixtures/secrets-repo

The `.reposcanner-exclude` file stops these fake secrets being reported when the whole RepoScanner repository is scanned.
The expected findings are listed in `tests/RepoScanner.Scanner.Tests/Secrets/FixtureSecrets.cs`.
