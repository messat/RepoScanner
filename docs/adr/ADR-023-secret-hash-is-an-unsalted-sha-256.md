# ADR-023: The secret hash is an unsalted SHA-256

- **Status:** Accepted
- **Date:** 2026-09-25
- **Source:** Decided while implementing Issue 3 (#14), task #18

## Context

A secret finding must be recognised as the same secret across scans and across files, without the secret itself being stored anywhere: the raw value is never stored (implementation brief ADR-011, Issue 2 design). The `Findings` table therefore holds a hash of it in `SecretHash`. The Issue 2 design recorded that an unsalted hash has a weakness, and gave this ADR to task #18.

## Decision

`SecretHash` is the SHA-256 of the secret's UTF-8 bytes, as 64 lower-case hex characters, with no salt and no key. `SecretHasher` in RepoScanner.Scanner computes it. Whitespace and the escapes `\n` and `\r` are removed from the secret first, so line endings and JSON escaping do not change it.

## Consequences

Anyone who can read the `Findings` table could recover a short or common secret by hashing guesses and comparing the results. Long random values, such as storage account keys, private keys, NuGet API keys and Entra client secrets, cannot be recovered that way. Only the scanner, dashboard and pipeline identities can read the table (ADR-008, ADR-014), and the data stays in UK West (ADR-015).

A random salt per finding was rejected: the same secret would no longer give the same hash, which is the reason for storing one. A keyed hash (HMAC) with its key in Key Vault was rejected for now: it would protect short secrets, but it adds a second Key Vault secret, and ADR-007 allows exactly one. It is the upgrade to make if short passwords turn out to be common among the findings. Changing the hash changes the fingerprint of every secret finding, so the switch would go through the reconciler (Issue 4).
