---
name: test
description: How to run tests and interpret the results. Use this when you need to run the test suite or write new tests.
---

# Test Skill

## How to Run Tests

Run all tests:

```bash
dotnet test SharpCoder.slnx
```

## Running a Targeted Subset

Before committing a change, run the tests for the touched namespaces/classes as a fast pre-commit self-check:

```bash
dotnet test SharpCoder.slnx --filter "FullyQualifiedName~<NamespaceOrTestClass>"
```

Join multiple filters with `|` to cover several classes in one run:

```bash
dotnet test SharpCoder.slnx --filter "FullyQualifiedName~ContextCompactorTests|FullyQualifiedName~CodingAgentTests"
```

## Reading Results

After running tests, look for the test summary line. Example output:

```
Passed!  - Failed:     0, Passed:   418, Skipped:     0, Total:   418, Duration: 13s
```

Record:
- **total_tests**: the Total count
- **passed_tests**: the Passed count
- **failed_tests**: the Failed count

## Opt-in Coverage

Coverage collection is opt-in, not the default:

```bash
dotnet test SharpCoder.slnx --collect:"XPlat Code Coverage" --results-directory ./TestResults
```

For coverage, parse the Cobertura XML in the TestResults directory:
```bash
cat TestResults/*/coverage.cobertura.xml | grep '<coverage' | head -1
```
The `line-rate` attribute is the coverage percentage (0.37 = 37%).

Caveat: the coverage collector has known failure modes (the SIGBUS/exit-135 test-host crash under parallel load; the WebApplicationFactory `BadImageFormatException` family). A coverage run must never be the single gating run; re-run without coverage before concluding anything.

## Writing New Tests

- Use **xUnit** as the test framework
- Place tests in the `tests/` directory
- Name test methods: `MethodName_Scenario_ExpectedBehavior`
- Follow Arrange-Act-Assert pattern
