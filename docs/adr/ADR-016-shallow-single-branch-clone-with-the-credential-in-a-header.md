# ADR-016: Shallow, single-branch clone with the credential in a header

- **Status:** Accepted
- **Source:** Code Repo Analysis Dashboard, Spike Findings & ADRs v2 (canonical for ADR-001 to ADR-019)

## Context

The replica's ephemeral disk is roughly 2 GiB at 0.5 vCPU, full history for the largest repositories is far larger than their working tree, and Azure DevOps throttles clones. The clone URL Azure DevOps offers embeds a username, which is exactly the pattern this tool flags.

## Decision

git clone --depth 1 --single-branch of the default branch into the container's temp directory, run as a process with ArgumentList, the PAT passed as -c http.extraheader with a Basic credential, GIT_TERMINAL_PROMPT=0, an eight-minute cap, and DriveInfo logged before and after. LibGit2Sharp was rejected because it has no reliable shallow clone.

## Consequences

The scan is a current-state scan of HEAD. A secret committed and later removed stays in history and is not found, and feature branches are not scanned; recorded as a MoSCoW Could-have with a one-off full-history sweep as future work, and out of scope for the EPA. A wrong PAT fails in seconds rather than hanging to replicaTimeout. No credential appears in a URL, a log line or a configuration file.
