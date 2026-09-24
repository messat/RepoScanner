# ADR-022: One options class per configuration group, validated at start-up

- **Status:** Accepted
- **Date:** 2026-09-24
- **Source:** Decided while implementing Issue 1 (AB#2)

## Context

The implementation brief asks for the scanner's settings to be bound to `ScannerOptions` (record types) and validated with `ValidateDataAnnotations().ValidateOnStart()`. The settings fall into groups under `Scanner:` (Queue, Execution, Database, DevOps, Rules). The built-in data annotations validator checks one object's own properties and does not look inside nested objects, so a single `ScannerOptions` holding the groups would need extra code to validate them. Most classes also need only one group: the queue code has no reason to see the Azure DevOps PAT.

## Decision

Each settings group is its own sealed record, bound to its own section and validated on its own: `QueueOptions` is bound to `Scanner:Queue`. Each is registered with `Bind`, `ValidateDataAnnotations` and `ValidateOnStart`, so invalid settings stop the host at start-up rather than when first used. Rules that involve more than one property (exactly one of `ConnectionString` or `ServiceUri`) are implemented with `IValidatableObject`. A group is added only when a feature uses it, so the configuration never holds settings that nothing reads. An invalid configuration is logged as one line, without a stack trace, and the process exits with code 2.

## Consequences

The configuration keys are unchanged from the brief (`Scanner:Queue:Name` and so on). A class asks for `IOptions<TheGroup>` and receives only that group. Each group's validation is complete without custom code. `Program.CreateBuilder` gains one registration per group as features are added. The default poison queue name is derived from the queue name (`<Name>-poison`), which gives each lane its own poison queue as ADR-013 requires.
