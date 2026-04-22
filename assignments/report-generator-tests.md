# Test the Report Generator

You are given a small .NET 10 solution called `ReportGen` that produces a
sales report in three formats (CSV, JSON, Markdown). The source is in the
`ReportGen` folder next to this assignment file (one `Cli` project and one
`Core` library project, plus an `.slnx` solution). **There is no test
project yet — your job is to add one.**

## Goal

Write a complete, well-structured test suite for `ReportGen` that exercises
the Core library and the CLI end-to-end. The production code is in scope
for reading and understanding, but **must not be modified** — not even to
add `internal` visibility, `[InternalsVisibleTo]`, or new seams. If the
production code has a design flaw that blocks a test, document it in a
comment in the relevant test and work around it; do not edit the production
code.

## Conditions

- Target .NET 10 and the latest C# language version.
- Add a single test project to the existing `.slnx` solution.
- You choose the testing framework — pick whichever you think is the best
  fit for .NET 10 in 2026 and be prepared to defend the choice. Do **not**
  ship multiple test frameworks side-by-side.
- You may add NuGet packages if they are genuinely useful
  (assertion libraries, fakes, coverage tooling, etc.). Pin to a **released
  version** (no `-preview`, `-beta`, `-rc`, `-alpha`) unless you explain in
  a comment in the csproj why a pre-release is required.
- Prefer `dotnet add package <name>` over hand-writing `<PackageReference>`
  entries so the current version is resolved automatically.
- Nullable reference types must be enabled in the test project.
- `TreatWarningsAsErrors` must be enabled in the test project.
- The whole solution (existing projects + your new test project) must
  build cleanly with `dotnet build` — zero warnings, zero errors.

## What the test suite must contain

At minimum the suite must include:

1. **Unit tests** for the Core library. Each test must have a single
   responsibility — one assertion topic per test, descriptive name,
   clear Arrange / Act / Assert separation. Cover the public API of
   `ReportGen.Core` (report generation, each formatter, the formatter
   factory, the seed data source). Edge cases count: empty input,
   culture-sensitive formatting, rounding, ordering of top products,
   unknown format strings, etc.
2. **Integration tests** that drive the `ReportGen.Cli` executable
   end-to-end. These must launch the CLI as a real process (or via the
   project's `Main` entry point with redirected stdout/stderr — your
   call) and assert on the actual output. Cover at least:
   - `--format csv`, `--format json`, `--format markdown` produce the
     expected output on stdout.
   - `--format xml` (or any other unknown value) exits non-zero with a
     clear error message.
   - `--format json --output <file>` writes the file and leaves stdout
     empty (or near-empty).
   - Case-insensitive format handling if the CLI supports it.
3. **Code coverage reporting.** Wire up coverage collection so that
   running `dotnet test` (or a clearly-documented alternative command)
   produces a coverage report. State the coverage number you achieved
   in a short note in the test project's folder (e.g. a `COVERAGE.md`
   or a comment at the top of the test csproj). Aim high on
   `ReportGen.Core`; the CLI entry point can be lower if integration
   tests already exercise it.

## Constraints on how the tests are written

- **Single responsibility per test.** A test that asserts five unrelated
  things about one formatter is not acceptable — split it.
- Test names should read like sentences (`Method_Scenario_Result` or
  `Should_…_When_…` — consistent within the project).
- No shared mutable state between tests. Each test must be able to run
  in isolation and in parallel.
- No `Thread.Sleep`, no flaky time-based assertions. If the code under
  test depends on wall-clock time, isolate it or supply a fixed input.
- Integration tests must clean up any temp files they create.
- Group tests into folders that mirror the `ReportGen.Core` / `ReportGen.Cli`
  structure (e.g. `Core/Formatting/CsvReportFormatterTests.cs`,
  `Cli/CliIntegrationTests.cs`). Don't put everything in one file.

## Acceptance criteria

The following must all be true before the assignment is complete:

- [ ] `dotnet build` on the whole solution succeeds with zero warnings.
- [ ] `dotnet test` runs the full suite and all tests pass.
- [ ] The test project is listed in `ReportGen.slnx`.
- [ ] Both unit tests and integration tests exist (a run that has only
      unit tests, or only integration tests, does not satisfy the
      assignment).
- [ ] A coverage report is produced and its headline number is recorded
      somewhere obvious.
- [ ] Production code (everything under `ReportGen.Cli` and
      `ReportGen.Core`) is unchanged compared to the starting snapshot,
      apart from the `.slnx` gaining one project reference.
- [ ] No stray files, scratch folders, or abandoned scaffolding in the
      solution root. The working directory contains exactly the projects
      that belong to the solution.

## Out of scope

- Changing the behaviour or structure of `ReportGen.Cli` or
  `ReportGen.Core`.
- Mutation testing, property-based testing, fuzz testing — nice to
  have, not required.
- UI tests, load tests, performance benchmarks.
- CI configuration (GitHub Actions etc.) — focus on the local
  `dotnet test` experience.

## Getting started

The harness will copy the starting source tree into your working directory
before you begin — the files from the `ReportGen` solution will already be
in place when you start. Run `dotnet build` first to confirm the starting
state is clean, then add your test project.
