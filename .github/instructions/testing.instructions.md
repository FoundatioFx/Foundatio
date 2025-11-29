---
description: "C# testing guidelines"
applyTo: "tests/**/*.cs"
---

# Testing Guidelines (C#)

## Framework & Best Practices

- Follow Microsoft's [unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices).
- Use xUnit as the primary testing framework.

## Test Principles

- **Fast & Isolated**: Tests should execute quickly and not depend on external factors or the order of execution.
- **Repeatable & Self-Checking**: Tests must be consistent and validate their own outcomes without manual checks.
- **Timely**: Write tests alongside your code to ensure relevance and improve design.

## Test Structure & Naming

- Write complete, runnable tests—no placeholders or TODOs.
- Use clear, descriptive naming conventions for test methods:
    - `MethodName_StateUnderTest_ExpectedBehavior`
- Follow AAA pattern (Arrange, Act, Assert).

## Test Organization

- Use `[Theory]` and `[InlineData]` for parameterized tests.
- Implement proper setup and teardown using constructors and `IDisposable`.
- Tests are organized to mirror the main code structure (e.g., `Storage/` in both `src` and `tests`).

## Integration Testing

- Inject `ITestOutputHelper` into the test class constructor to get access to the test output.
- Isolate dependencies using test containers, in-memory providers, or stubs to ensure reliable test execution.
- Verify data persistence and side effects.
