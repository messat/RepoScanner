# ADR-028: JSON log lines on stdout outside Development

- **Status:** Accepted
- **Date:** 2026-10-02
- **Source:** Decided while implementing Issue 10 (#61), task #65

## Context

Container Apps sends each line the scanner writes to stdout to Log Analytics as one text value. The scanner's log calls name every value (`{CorrelationId}`, `{ScanRunStatus}`), but the console's text layout turns each line into a sentence, so a search can only match words, and an alert depends on exact wording. Two loggers write to the console: the host's, and the start-up logger that reports why the scanner could not start or was stopped.

## Decision

Outside Development, both loggers write one CLEF JSON object per line with `RenderedCompactJsonFormatter` from `Serilog.Formatting.Compact`: the time, the finished message, the level, any exception, and every property at the top level, including `SourceContext`, and the `CorrelationId`, `ProjectName` and `RepositoryName` from the scan's logging scope. Development writes text. `Scanner:Logging:Format` (`Text` or `Json`, in any case) overrides the environment; any other value stops the scanner at start-up with exit code 2.

## Consequences

A search unpacks a line with `parse_json(Log)` and filters on any property. An exception and its stack trace stay in one row. Property names become part of the searches and alerts, so renaming one breaks them: tests pin the lines the alerts rely on. Raw JSON is harder to read than text, which is why Development keeps text, and a container can be told to print text with `Scanner__Logging__Format=Text`. CLEF leaves out the level for Information lines, and the rendered message quotes string values. One more package to keep up to date. The start-up logger exists before configuration is read, so it takes the setting from environment variables and the command line only, not from appsettings files. Searching the JSON in Log Analytics is described in the Issue 10 design (`docs/design/2026-10-02-observability-and-run-summary.md`, Searches).
