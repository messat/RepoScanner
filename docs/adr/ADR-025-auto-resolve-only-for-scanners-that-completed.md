# ADR-025: Only a scanner that completed can resolve its findings

- **Status:** Accepted
- **Date:** 2026-09-27
- **Source:** Decided while implementing Issue 4 (#22), task #25

## Context

A scan resolves a stored finding when it no longer finds it (Issue 4, rule 3). A scanner that fails partway, or does not run at all, finds nothing, and to the reconciler that looks the same as every one of its findings having been fixed. One failed run of the secret scanner would then mark every secret in the repository as resolved, and the dashboard would show a clean repository that was never checked. The spike document (Data model, ScanRun) makes the same point: a repository that shows no findings because its scan silently failed is worse than having no tool.

## Decision

`FindingReconciler.Reconcile` is given `completedScannerTypes`: the scanner types that ran and succeeded in this run. A stored finding that was not found is resolved only if its own scanner type is in that set; otherwise it is left exactly as it was, with no event. Findings of the scanners that did complete are resolved as usual.

In Issue 4, local mode passes every scanner it ran, because a scanner that throws still fails the whole run before anything is saved. From Issue 5 (task #74), the scan carries on after one scanner fails and passes only the scanners that succeeded; the queue loop in Issue 7 reuses it.

## Consequences

While a scanner keeps failing, its findings keep their last status, even if they have since been fixed. That is stale rather than wrong: its `ScannerResults` row shows `Failed`, and from Issue 5 the run shows `PartiallyCompleted`, so the dashboard can show that those results are out of date. A scanner type that is not run at all, such as the package scanner in local mode, never resolves its findings either.

Resolving anyway was rejected: it would mark every finding of a failed scanner as fixed. Skipping reconciliation for the whole run when any scanner fails was rejected: one broken scanner would stop every other scanner's findings from resolving.
