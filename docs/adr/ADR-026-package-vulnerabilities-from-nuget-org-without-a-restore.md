# ADR-026: Package vulnerabilities come from nuget.org's vulnerability files, without a restore

- **Status:** Accepted
- **Date:** 2026-09-28
- **Source:** Decided while implementing Issue 5 (#42), task #44

## Context

The package scanner must know which of the package versions a repository's project files declare have known vulnerabilities. It runs for every repository, in a container with no build tools, with a read-only credential for Azure DevOps and none for private NuGet feeds. The repositories include old-style .NET Framework projects, such as Dynamics 365 plug-ins, which list their packages with `<PackageReference>` but do not restore on Linux.

## Decision

The scanner reads the project files as XML (#43), without evaluating them. It downloads nuget.org's `VulnerabilityInfo/6.7.0` files, found through the service index, once per execution, and matches the declared versions against them in memory with `NuGet.Versioning`. A floating version or a range is first resolved against the package's version list (`PackageBaseAddress/3.0.0`), which is also downloaded once per package per execution. This is the approach nuget.org documents for a client that checks many packages.

## Consequences

Only the packages the project files list directly are checked. Their dependencies (transitive packages) are known only after a restore, so they are not checked; the README records this as a limitation. The scanner image needs no .NET SDK and no feed credentials. Each execution downloads about 650 KB of vulnerability data (599 packages on 2026-09-28) plus a small version list per floating reference. The advisories are nuget.org's copy of the GitHub Advisory Database, so they are as complete and as current as that.

When nuget.org cannot be reached after the retries, the package scanner fails on its own: the other scanners carry on and the run is `PartiallyCompleted` (#74, #32), and the failure is remembered for the rest of the execution so later repositories do not wait for the retries again.

Three alternatives were rejected:

- `dotnet list package --vulnerable`, or restore's own audit: it needs the .NET SDK in the image, a full restore of every project, credentials for private feeds, and old-style .NET Framework projects to restore on Linux.
- `NuGet.Protocol`, NuGet's client library: it is built around NuGet's settings files and disk cache, and is harder to fake in tests.
- One request per package to the package metadata resource: nuget.org's documentation says downloading the vulnerability files is much faster than many such requests.
