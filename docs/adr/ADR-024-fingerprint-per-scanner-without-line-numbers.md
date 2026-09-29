# ADR-024: A fingerprint per scanner type, without line numbers

- **Status:** Accepted
- **Date:** 2026-09-27
- **Source:** Decided while implementing Issue 4 (#22), task #27

## Context

Each scan compares what it found with the findings already stored for the repository (Issue 4). For that, a finding needs an identity that stays the same from one scan to the next, so its status, suppression (ADR-019) and history stay on one row. The `Findings` table holds that identity in `Fingerprint`, unique per repository (Issue 2 design).

Line numbers cannot be part of it: adding one line near the top of a file moves every finding below it, and each would look new, leaving its suppression behind on a row that is then resolved. *Code Repo Analysis Dashboard, Spike Findings & ADRs v2* (Data model, "Three reasoning points") sets out what identifies a finding for each scanner.

## Decision

The fingerprint is the SHA-256 of the finding's parts joined with `|`, as UTF-8, written as 64 lower-case hex characters. `FingerprintCalculator` in RepoScanner.Scanner computes it.

| Scanner | Parts, in order |
|---|---|
| Secret | Rule ID, file path, the secret's hash (ADR-023) |
| Package | Rule ID, project file path, package name in lower case, package version, advisory URL |
| Code pattern | Rule ID, file path, the hash of the matched code |

- The line number, message, severity and masked preview are never part of it: they describe a finding, they do not identify it.
- NuGet package IDs ignore case, so the package name is lower-cased.
- The code-pattern scanner (Issue 6) supplies the hash of the matched code in `RawFinding.SnippetHash`, and decides how the code is tidied before it is hashed. The hash is not stored: the fingerprint carries it.
- A finding without one of its parts is a scanner bug, and is refused rather than saved.

Two differences from the spike document:

- **The repository is not a part.** The unique index is on `(RepositoryId, Fingerprint)`, so the same fingerprint in two repositories is already two findings.
- **A secret has no occurrence number, so the same secret twice in one file is one finding.** The fix for a leaked secret is to change it, not to remove its copies. An occurrence number depends on the order of the copies: deleting the first gives the second its number, so a suppression could move to a different copy.

## Consequences

A finding keeps its fingerprint when code above it changes, so its suppression and history follow it. The secret formula is the one Issue 3's interim code used, so findings already saved keep their fingerprints.

Renaming or moving a file changes the fingerprints of its findings: they are resolved and found again as new, and their suppressions do not follow. The spike document accepts this rather than solving it. Because the version is a part, upgrading a package to another vulnerable version resolves the old finding and opens a new one, which is correct: it is a different advisory to check.

Changing any formula, or how Issue 6 tidies the code, changes existing fingerprints in the same way: every affected finding is resolved and found again as new, and loses its suppression. Such a change needs a new ADR that says so.

## Amendments

**2026-09-27 (Issue 5, #43):** For a package, the file path is the file where the version is set: the `.csproj`, or `Directory.Packages.props` when the version is set centrally (Central Package Management). One vulnerable version set centrally is then one finding, in the place it is fixed, rather than one per project that uses it. The formula is unchanged; only what "project file path" names is made exact.

**2026-09-28 (Issue 5, #75):** `PKG002`, a deprecated package version, uses the same package formula. A deprecation has no advisory, so the link part is the version's nuget.org page, with the lower-case package ID and the normalised version, for example `https://www.nuget.org/packages/microsoft.data.sqlclient/5.1.0`, so writing the ID or version differently in a project does not change it. The rule ID keeps one version's `PKG001` and `PKG002` findings apart. The formula is unchanged.

**2026-09-28 (Issue 6, #49):** For a code pattern, the hash of the matched code is the SHA-256, as 64 lower-case hex characters, of three parts joined with `|` as UTF-8: the names of the types the code sits in, outermost first, joined with `.`; the name of the member it sits in (method, constructor, property, indexer, event or field); and the matched code's tokens joined by single spaces. A part is empty when there is no such type or member. Spacing, line breaks and comments are not tokens, so reformatting a file does not change a fingerprint. Renaming the type or member, or changing the matched code, does, as renaming the file does. Two identical matches of one rule in one member are one finding, as for a secret. The formula is unchanged.
