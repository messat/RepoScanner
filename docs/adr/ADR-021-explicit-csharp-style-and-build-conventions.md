# ADR-021: Explicit C# style and build conventions, enforced by the build

- **Status:** Accepted
- **Date:** 2026-09-24
- **Source:** Decided while implementing Issue 1 (AB#2)

## Context

The codebase is also a learning record: a reader should be able to see from each file which libraries it depends on and where the program starts, without relying on compiler features that add code invisibly. Six projects (three source, three test) need the same rules, and rules that are only written down drift as soon as a new project is added from a template.

## Decision

C# is written explicitly: implicit usings are off, so every file lists its own `using` directives; there are no top-level statements, so the entry point is `Program.Main` in a `class Program`; namespaces are block-scoped.

These rules are enforced by the build, not by convention. `Directory.Build.props` at the repository root sets them for every project, together with `TargetFramework net10.0`, nullable reference types and `TreatWarningsAsErrors`. `.editorconfig` turns top-level statements (IDE0211) and file-scoped namespaces (IDE0160) into warnings, and `EnforceCodeStyleInBuild` makes the build report them, so they fail the build. `global.json` pins the .NET 10 SDK.

Package versions are set once in `Directory.Packages.props` (Central Package Management); each `.csproj` lists the packages it uses without a version, and a version in a `.csproj` fails restore (NU1008).

Test coverage uses `coverlet.msbuild`, because `coverlet.collector` can measure coverage but cannot fail a run. Every `*.Tests` project measures only the project it is named after, and `dotnet test` fails below 80% line coverage for Core and Scanner. Data is measured and reported but not enforced.

## Consequences

Each file's dependencies and the program's entry point are visible where a reader would look. A new project inherits every rule, but templates such as `dotnet new classlib` and `dotnet new nunit` write their own `ImplicitUsings`, `Nullable` and global `Using` entries, which override the shared settings and must be removed each time. Coverage is checked by `dotnet test`, not `dotnet build`. The .NET terminal logger hides the coverage table in an interactive terminal; `--tl:off` shows it.
