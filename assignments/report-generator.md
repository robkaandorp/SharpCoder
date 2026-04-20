# Assignment: Sales Report Generator CLI

Build a small command-line tool, **`reportgen`**, that turns a fixed seed
dataset of sales records into a human- and machine-readable report, rendered
in three different formats.

You have full freedom over the internal design — solution layout, class
structure, naming, which third-party NuGet packages to use, and how the code
is organised are entirely up to you. Only the conditions and the end result
below are fixed.

## Conditions

- Targets **.NET 10** and uses the **latest C# language version**.
- May depend on NuGet packages where they genuinely improve maintainability.
- Ships with **unit tests** that cover the non-trivial logic. `dotnet test`
  must pass.
- Builds cleanly with `dotnet build` — no warnings escalated by the compiler,
  no errors.
- The code should be idiomatic, readable, and easy for another developer to
  extend with a new output format later.

## Seed dataset

The tool works on the following sales records. They may be embedded in code
or loaded from a file that ships with the project — your call.

| Date       | Region | Product      | Quantity | Unit price |
|------------|--------|--------------|---------:|-----------:|
| 2026-01-05 | North  | Widget       |       10 |      9.99  |
| 2026-01-07 | South  | Widget       |        4 |      9.99  |
| 2026-01-09 | North  | Gadget       |        2 |     24.50  |
| 2026-01-12 | East   | Gizmo        |        7 |     14.00  |
| 2026-01-14 | West   | Widget       |       12 |      9.99  |
| 2026-01-18 | South  | Gadget       |        5 |     24.50  |
| 2026-01-21 | East   | Contraption  |        1 |     99.00  |
| 2026-01-25 | North  | Gizmo        |        3 |     14.00  |
| 2026-01-28 | West   | Gadget       |        6 |     24.50  |
| 2026-01-30 | South  | Contraption  |        2 |     99.00  |

Revenue for a record is `quantity × unit price`.

## CLI

```
reportgen --format <csv|json|markdown> [--output <file>]
```

- `--format` is required and must be one of `csv`, `json`, or `markdown`
  (case-insensitive). Any other value produces a clear error message and a
  non-zero exit code.
- `--output` is optional. When supplied, the report is written to that file;
  otherwise it is written to standard output.
- A `--help` option describes the flags.

## Report contents

Regardless of format, the report contains the same three sections, in this
order:

1. **Header** — a title and a generated-at timestamp (ISO-8601, UTC).
2. **Summary** — total revenue, total units sold, and the number of records.
3. **Breakdown**
   - Revenue per region (all regions present in the dataset, sorted by
     revenue descending).
   - Top 3 products by revenue (product name and revenue, sorted descending).

Currency values are formatted with two decimal places using the invariant
culture. Dates use ISO-8601.

### Format-specific expectations

- **CSV** — one logical section per block, separated by a blank line. Each
  section has a header row and one or more data rows. A human opening the
  file in a text editor or spreadsheet should understand it immediately.
- **JSON** — a single well-formed JSON document. Field names are camelCase.
  Parsable by any standard JSON reader without post-processing.
- **Markdown** — a single document with `#`/`##` headings for the sections
  and GitHub-flavoured tables for the breakdown data. Readable as-is in a
  Markdown viewer.

## Acceptance criteria

The following must all be true before the assignment is complete:

- [ ] `dotnet build` succeeds.
- [ ] `dotnet test` succeeds and tests exercise the report logic, not only
      trivial getters/setters.
- [ ] `reportgen --format csv`, `--format json`, and `--format markdown`
      each produce output that matches the "Report contents" section above.
- [ ] `reportgen --format xml` (or any other unknown value) prints a clear
      error and exits non-zero.
- [ ] `reportgen --format json --output report.json` writes the report to
      `report.json` and prints nothing (or only a brief status line) to
      stdout.
- [ ] The same report data drives all three formats — adding a fourth
      format in the future should require changing only a small, isolated
      part of the code.

## Out of scope

- Reading arbitrary user-supplied data sources. The seed dataset above is
  the only input.
- Localisation, internationalisation, or configurable currency symbols.
- Persisting state between runs, logging frameworks, telemetry, or
  dependency-injection containers unless you genuinely want them.
