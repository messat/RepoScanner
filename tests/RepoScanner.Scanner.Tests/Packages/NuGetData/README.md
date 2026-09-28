# Canned nuget.org responses

`FakeNuGet` answers the package tests from these files, so no test reaches the internet. They are copies of what nuget.org returned on 2026-09-28:

| File | nuget.org address | Changed |
|---|---|---|
| `service-index.json` | `https://api.nuget.org/v3/index.json` | No |
| `vulnerability-index.json` | the `VulnerabilityInfo/6.7.0` resource | No |
| `vulnerability-base.json` | the `base` page it lists | Only the fixture's packages and Microsoft.Data.SqlClient are kept, with their entries unchanged |
| `vulnerability-update.json` | the `update` page it lists | No: it was empty |
| `versions/<id>.json` | the `PackageBaseAddress/3.0.0` resource, `<id>/index.json` | No |
| `registrations/<id>/index.json`, and `registrations/<id>/page/...` for a package whose pages are separate documents | the `RegistrationsBaseUrl/3.6.0` resource: the package's metadata, with each version's deprecation (#75) | Only the page that holds the versions the tests use, and within it those versions only |

A package with no file under `versions/` or `registrations/` is answered with 404, as nuget.org answers for a package it does not know.

The files are kept here as plain JSON so they can be read, but nuget.org sends the vulnerability index and its pages, and the package metadata, gzip-compressed, with `Content-Encoding: gzip`, even when the client does not ask for it. `FakeNuGet` compresses those the same way, so the tests read what the scanner really receives. Tools such as PowerShell's `Invoke-WebRequest` decompress silently, so to see what nuget.org really sends, use `curl.exe -D - -o reply.bin <address>` without `--compressed`.
