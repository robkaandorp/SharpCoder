---
name: test
description: How to run tests and interpret the results. Use this when you need to run the test suite or write new tests.
---

# Test Skill

## How to Run Tests

**Prerequisite:** If `command -v dotnet` fails, load and follow the `install-dotnet-sdk` skill first; do not record the exit-127 run as a test result.

Run all tests:

```bash
dotnet test SharpCoder.slnx
```

## Running a Targeted Subset

Before committing a change, run the tests for the touched namespaces/classes as a fast pre-commit self-check:

MTP uses xUnit v3 filter options instead of VSTest's `FullyQualifiedName` filter.
A class filter selects matching test classes; repeated `--filter-class` options are
combined as alternatives. These verified examples select 80 and 122 tests,
respectively:

```bash
dotnet test tests/SharpCoder.Tests --filter-class '*ContextCompactorTests'
dotnet test tests/SharpCoder.Tests --filter-class '*CodingAgentTests' --filter-class '*ContextCompactorTests'
```

Other supported filter forms can target a namespace, method, or query. This
namespace example selected 729 tests:

```bash
dotnet test tests/SharpCoder.Tests --filter-namespace 'SharpCoder.Tests'
```

Use `--filter-method` to select a method name; this verified class-name glob
selected 80 tests:

```bash
dotnet test tests/SharpCoder.Tests --filter-method '*ContextCompactorTests*'
```

`--filter-query` accepts the xUnit v3 query syntax (not a glob); verify query
syntax before using it. Legacy `--filter "FullyQualifiedName~…"` and the `|`
join are not supported by MTP here and can make the solution run fail because
projects with no matching tests return a nonzero exit code.

## Reading Results

After running tests, look for the MTP summary. A verified full-suite run produced:

```
Test run summary: Passed!
  total: 1122
  failed: 0
  succeeded: 1122
  skipped: 0
  duration: 1m 01s 162ms
```

Record:
- **total_tests**: `total`
- **passed_tests**: `succeeded`
- **failed_tests**: `failed`

## Opt-in Coverage

Coverage collection is opt-in, not the default:

```bash
dotnet test SharpCoder.slnx --coverage --coverage-output-format cobertura --results-directory ./TestResults
```

A verified run produced Cobertura reports under `TestResults/` (one per test
assembly), for example `TestResults/35f13291-4c52-41ab-9246-365681a18bf6.cobertura.xml`
and `TestResults/b44360cc-4501-4a82-bc5b-dd39742c2f78.cobertura.xml`. To inspect
the report, use:

```bash
cat TestResults/*.cobertura.xml | grep '<coverage' | head -1
```

The `line-rate` attribute is the coverage percentage (0.37 = 37%). The prior
coverage caveat (SIGBUS/exit-135 under parallel load and WebApplicationFactory
`BadImageFormatException`) was not re-observed in this MTP coverage run.

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern
