# ADR-018: Code-pattern rules from a Roslyn syntax walker, justified by analyser IDs

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

The proposal named SQL injection as the insecure-pattern example. EF Core parameterises by default, so raw-SQL injection is rare in a modern .NET codebase, and a scanner that finds nothing has no business value.

## Decision

A CSharpSyntaxWalker rule set (CP001 to CP006 in the implementation brief) drawn from these candidates, each citing the Microsoft analyser ID it mirrors: new HttpClient() per call rather than IHttpClientFactory, empty catch blocks, disabled TLS certificate validation (CA5359), Random for security tokens (CA5394), string concatenation into FromSqlRaw or SqlCommand (CA2100), BinaryFormatter (SYSLIB0011) and AllowAnyOrigin(). The analysers themselves are not run; the ID is the justification reference.

## Consequences

Every finding points at documented Microsoft guidance a developer can look up. The rule set changes the proposal's example and must be confirmed with the technical manager. Rules are syntax-only, so a pattern hidden behind a helper method is missed; accepted for a first release.
