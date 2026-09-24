# ADR-020: Branching strategy and branch policies

- **Status:** Accepted
- **Date:** 2026-09-24
- **Source:** Decided while implementing Issue 1 (AB#2)

## Context

The implementation brief asked for feature branches created from `main` and pull requests into `main`. The repository was then set up with a `develop` branch as its default, so that `main` only ever holds released code. The project is built by one developer as EPA evidence, so every change needs to be traceable to a work item on the board, and nothing should reach a shared branch without going through a pull request, including changes made with an AI assistant.

## Decision

`develop` is the default branch. Work happens on `feature/<work-item-id>-<short-name>` branches created from `develop`, and reaches `develop` only through a pull request. Releases go from `develop` to `main`, also by pull request.

Both `develop` and `main` have blocking branch policies: a minimum of one reviewer (the requestor may approve their own changes, as there is one developer), a linked work item, and all comments resolved. With a required policy in place, Azure Repos rejects direct pushes (`TF402455: Pushes to this branch are not permitted`). Merge types are not restricted; "Merge (no fast forward)" is preferred so the test-first commit history stays visible.

Commit messages are plain sentences starting "This commit ...", ending with the Azure Repos work item mention `#<task id>`, so each commit links to its task when pushed. `AB#<id>` is the GitHub syntax and is not used. Where possible there is one commit per task.

## Consequences

Every change on `develop` and `main` traces back to a pull request and a work item, and every commit to a task. Self-approval means the review is done by the author, which is an accepted limitation of a one-developer project; the policy still forces the pull request, its description and its linked work item to exist. Anyone granted "Bypass policies when pushing" could still push directly, so that permission is left unassigned. A build validation policy is added once a pipeline exists.
