# Canned nuget.org responses

`FakeNuGet` answers the package tests from these files, so no test reaches the internet. They are copies of what nuget.org returned on 2026-09-28:

| File | nuget.org address | Changed |
|---|---|---|
| `service-index.json` | `https://api.nuget.org/v3/index.json` | No |
| `vulnerability-index.json` | the `VulnerabilityInfo/6.7.0` resource | No |
| `vulnerability-base.json` | the `base` page it lists | Only the fixture's packages are kept, with their entries unchanged |
| `vulnerability-update.json` | the `update` page it lists | No: it was empty |
| `versions/<id>.json` | the `PackageBaseAddress/3.0.0` resource, `<id>/index.json` | No |

A package with no file under `versions/` is answered with 404, as nuget.org answers for a package it does not know.
