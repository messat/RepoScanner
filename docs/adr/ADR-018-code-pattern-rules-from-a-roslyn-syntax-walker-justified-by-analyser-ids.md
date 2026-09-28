# ADR-018: Code-pattern rules from a Roslyn syntax walker, justified by analyser IDs

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

The proposal named SQL injection as the insecure-pattern example. EF Core parameterises by default, so raw-SQL injection is rare in a modern .NET codebase, and a scanner that finds nothing has no business value.

## Decision

A CSharpSyntaxWalker rule set (CP001 to CP006 in the implementation brief) drawn from these candidates, each citing the Microsoft analyser ID it mirrors: new HttpClient() per call rather than IHttpClientFactory, empty catch blocks, disabled TLS certificate validation (CA5359), Random for security tokens (CA5394), string concatenation into FromSqlRaw or SqlCommand (CA2100), BinaryFormatter (SYSLIB0011) and AllowAnyOrigin(). The analysers themselves are not run; the ID is the justification reference.

## Consequences

Every finding points at documented Microsoft guidance a developer can look up. The rule set changes the proposal's example and must be confirmed with the technical manager. Rules are syntax-only, so a pattern hidden behind a helper method is missed; accepted for a first release.

## Amendments

**2026-09-28 (Issue 6, #55):** The rule set is `CP001` to `CP006`, set out in the Issue 6 design: SQL built by joining strings (CA2100); insecure deserializers and Newtonsoft `TypeNameHandling` other than `None` (CA2300, CA2305, CA2310, CA2315, CA2326); certificate validation switched off (CA5359); weak hash and cipher algorithms (CA5350, CA5351); `AllowAnyOrigin()` in a CORS policy; and empty catch blocks (related to CA1031). Two candidates are left out: `new HttpClient()` per call, which is a reliability problem rather than a security one, and `Random` for security tokens, because a syntax-only rule cannot tell a token from any other use and would flag every `Random`. No Microsoft analyser covers `AllowAnyOrigin()`, so `CP005` cites Microsoft's *Enable CORS* page instead of an ID; no ID is made up. Each finding's message ends with its reference and link. The set still has to be confirmed with the technical manager.
