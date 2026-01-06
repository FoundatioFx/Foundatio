# Agent Guidelines for Foundatio

You are an expert .NET engineer working on Foundatio, a production-grade library used by thousands of developers. Your changes must maintain backward compatibility, performance, and reliability. Approach each task methodically: research existing patterns, make surgical changes, and validate thoroughly.

**Craftsmanship Mindset**: Every line of code should be intentional, readable, and maintainable. Write code you'd be proud to have reviewed by senior engineers. Prefer simplicity over cleverness. When in doubt, favor explicitness and clarity.

## Repository Overview

Foundatio provides pluggable building blocks for distributed .NET applications:

- **Caching** (`ICacheClient`) - In-memory, Redis, hybrid caching with expiration
- **Queues** (`IQueue<T>`) - Message queuing with work items, delays, retries
- **Locks** (`ILockProvider`) - Distributed locking for coordination
- **Messaging** (`IMessageBus`) - Pub/sub messaging between services
- **Jobs** (`IJob`) - Background job processing with scheduling
- **Storage** (`IFileStorage`) - Unified file storage abstraction
- **Resilience** - Retry policies, circuit breakers, rate limiting

Design principles: **interface-first**, **testable**, **swappable implementations**, **in-memory + external providers**.

## Quick Start

```bash
# Build
dotnet build Foundatio.slnx

# Test
dotnet test Foundatio.slnx

# Format code
dotnet format Foundatio.slnx
```

**Note**: When building within a workspace, use `Foundatio.All.slnx` instead to include all Foundatio projects in the build and test cycle.

## Project Structure

```text
src
├── Foundatio                         # Core abstractions and in-memory implementations
│   ├── Caching                       # ICacheClient - caching with expiration
│   ├── Queues                        # IQueue<T> - message queuing
│   ├── Lock                          # ILockProvider - distributed locking
│   ├── Messaging                     # IMessageBus - pub/sub messaging
│   ├── Jobs                          # IJob - background job processing
│   ├── Storage                       # IFileStorage - unified file API
│   ├── Resilience                    # Retry policies, circuit breakers
│   ├── Serializer                    # ISerializer abstractions
│   └── Extensions                    # Extension methods for DI and utilities
├── Foundatio.Extensions.Hosting      # ASP.NET Core hosting integration
└── Foundatio.TestHarness             # Shared test base classes
tests
└── Foundatio.Tests                   # Unit and integration tests
samples                               # Sample applications
benchmarks                            # Performance benchmarks
docs                                  # Documentation site
```

## Coding Standards

### Style & Formatting

- Follow `.editorconfig` rules and [Microsoft C# conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Run `dotnet format` to auto-format code
- Match existing file style; minimize diffs
- No code comments unless necessary—code should be self-explanatory

### Architecture Patterns

- **Interface-first design**: All core features expose interfaces (`ICacheClient`, `IQueue<T>`, `IFileStorage`)
- **Dependency Injection**: Use constructor injection; extend via `IServiceCollection` extensions
- **In-memory implementations**: Always provide for testing and local development
- **Naming**: `Foundatio.[Feature]` for projects, `I[Feature]` for interfaces
- **External providers**: Redis, Azure, AWS implementations live in separate repositories

### Code Quality

- Write complete, runnable code—no placeholders, TODOs, or `// existing code...` comments
- Use modern C# features: pattern matching, nullable references, `is` expressions, target-typed `new()`
- Follow SOLID, DRY principles; remove unused code and parameters
- Clear, descriptive naming; prefer explicit over clever
- Use `AnyContext()` (e.g., `ConfigureAwait(false)`) in library code (not in tests)
- Prefer `ValueTask<T>` for hot paths that may complete synchronously
- Always dispose resources: use `using` statements or `IAsyncDisposable`
- Handle cancellation tokens properly: check `token.IsCancellationRequested`, pass through call chains

### Common Patterns

- **Async suffix**: All async methods end with `Async` (e.g., `GetAsync`, `SetAsync`)
- **CancellationToken**: Last parameter, defaulted to `default` in public APIs
- **Extension methods**: Place in `Extensions/` directory, use descriptive class names (e.g., `CacheClientExtensions`)
- **Logging**: Use structured logging with `ILogger`, log at appropriate levels
- **Exceptions**: Use `ArgumentException.ThrowIfNullOrEmpty(parameter)` for validation. For feature-specific errors, use consistent exception types: `StorageException` for file storage operations, `CacheException` for caching operations, `MessageBusException` for messaging operations. This ensures consumers get predictable exception types regardless of the underlying implementation (Redis, Azure, AWS, etc.). Throw `ArgumentNullException`, `ArgumentException`, `InvalidOperationException` with clear messages for general validation and operation errors.

### Single Responsibility

- Each class has one reason to change
- Methods do one thing well; extract when doing multiple things
- Keep files focused: one primary type per file
- Separate concerns: don't mix I/O, business logic, and presentation
- If a method needs a comment explaining what it does, it should probably be extracted

### Performance Considerations

- **Avoid allocations in hot paths**: Use `Span<T>`, `Memory<T>`, pooled buffers
- **Prefer structs for small, immutable types**: But be aware of boxing
- **Cache expensive computations**: Use `Lazy<T>` or explicit caching
- **Batch operations when possible**: Reduce round trips for I/O
- **Profile before optimizing**: Don't guess—measure with benchmarks
- **Consider concurrent access**: Use `ConcurrentDictionary`, `Interlocked`, or proper locking
- **Avoid async in tight loops**: Consider batching or `ValueTask` for hot paths
- **Dispose resources promptly**: Don't hold connections/handles longer than needed

## Making Changes

### Before Starting

1. **Gather context**: Read related files, search for similar implementations, understand the full scope
2. **Research patterns**: Find existing usages of the code you're modifying using grep/semantic search
3. **Understand completely**: Know the problem, side effects, and edge cases before coding
4. **Plan the approach**: Choose the simplest solution that satisfies all requirements
5. **Check dependencies**: Verify you understand how changes affect dependent code

### Pre-Implementation Analysis

Before writing any implementation code, think critically:

1. **What could go wrong?** Consider race conditions, null references, edge cases, resource exhaustion
2. **What are the failure modes?** Network failures, timeouts, out-of-memory, concurrent access
3. **What assumptions am I making?** Validate each assumption against the codebase
4. **Is this the root cause?** Don't fix symptoms—trace to the core problem
5. **Will this scale?** Consider performance under load, memory allocation patterns
6. **Is there existing code that does this?** Search before creating new utilities

### Test-First Development

**Always write or extend tests before implementing changes:**

1. **Find existing tests first**: Search for tests covering the code you're modifying
2. **Extend existing tests**: Add test cases to existing test classes/methods when possible for maintainability
3. **Write failing tests**: Create tests that demonstrate the bug or missing feature
4. **Implement the fix**: Write minimal code to make tests pass
5. **Refactor**: Clean up while keeping tests green
6. **Verify edge cases**: Add tests for boundary conditions and error paths

**Why extend existing tests?** Consolidates related test logic, reduces duplication, improves discoverability, maintains consistent test patterns.

### While Coding

- **Minimize diffs**: Change only what's necessary, preserve formatting and structure
- **Preserve behavior**: Don't break existing functionality or change semantics unintentionally
- **Build incrementally**: Run `dotnet build` after each logical change to catch errors early
- **Test continuously**: Run `dotnet test` frequently to verify correctness
- **Match style**: Follow the patterns in surrounding code exactly

### Validation

Before marking work complete, verify:

1. **Builds successfully**: `dotnet build Foundatio.slnx` exits with code 0
2. **All tests pass**: `dotnet test Foundatio.slnx` shows no failures
3. **No new warnings**: Check build output for new compiler warnings
4. **API compatibility**: Public API changes are intentional and backward-compatible when possible
5. **Documentation updated**: XML doc comments added/updated for public APIs
6. **Interface documentation**: Update interface definitions and docs with any API changes
7. **Feature documentation**: Add entries to [docs/](docs/) folder for new features or significant changes
8. **Breaking changes flagged**: Clearly identify any breaking changes for review

### Error Handling

- **Validate inputs**: Check for null, empty strings, invalid ranges at method entry
- **Fail fast**: Throw exceptions immediately for invalid arguments (don't propagate bad data)
- **Meaningful messages**: Include parameter names and expected values in exception messages
- **Don't swallow exceptions**: Log and rethrow, or let propagate unless you can handle properly
- **Use guard clauses**: Early returns for invalid conditions, keep happy path unindented

## Security

- **Validate all inputs**: Use guard clauses, check bounds, validate formats before processing
- **Sanitize external data**: Never trust data from queues, caches, or external sources
- **Avoid injection attacks**: Use parameterized queries, escape user input, validate file paths
- **No sensitive data in logs**: Never log passwords, tokens, keys, or PII
- **Use secure defaults**: Default to encrypted connections, secure protocols, restricted permissions
- **Follow OWASP guidelines**: Review [OWASP Top 10](https://owasp.org/www-project-top-ten/)
- **Dependency security**: Check for known vulnerabilities before adding dependencies
- **No deprecated APIs**: Avoid obsolete cryptography, serialization, or framework features

## Testing

### Philosophy: Battle-Tested Code

Tests are not just validation—they're **executable documentation** and **design tools**. Well-tested code is:

- **Trustworthy**: Confidence to refactor and extend
- **Documented**: Tests show how the API should be used
- **Resilient**: Edge cases are covered before they become production bugs

### Framework

- **xUnit** as the primary testing framework
- **Foundatio.TestHarness** provides shared base classes for implementation testing
- Follow [Microsoft unit testing best practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices)

### Test-First Workflow

1. **Search for existing tests**: `dotnet test --filter "FullyQualifiedName~MethodYouAreChanging"`
2. **Extend existing test classes**: Add new `[Fact]` or `[Theory]` cases to existing files
3. **Write the failing test first**: Verify it fails for the right reason
4. **Implement minimal code**: Just enough to pass the test
5. **Add edge case tests**: Null inputs, empty collections, boundary values, concurrent access
6. **Run full test suite**: Ensure no regressions

### Test Principles (FIRST)

- **Fast**: Tests execute quickly
- **Isolated**: No dependencies on external services or execution order
- **Repeatable**: Consistent results every run
- **Self-checking**: Tests validate their own outcomes
- **Timely**: Write tests alongside code

### Naming Convention

Use the pattern: `MethodName_StateUnderTest_ExpectedBehavior`

Examples:

- `SetAsync_WithNullExpiration_RemovesTtl`
- `DequeueAsync_WhenEmpty_ReturnsNull`
- `AcquireAsync_WhenLockHeld_WaitsForRelease`

### Test Structure

Follow the AAA (Arrange-Act-Assert) pattern:

```csharp
[Fact]
public async Task GetAsync_WithExpiredKey_ReturnsNull()
{
    // Arrange
    var cache = new InMemoryCacheClient();
    await cache.SetAsync("key", "value", TimeSpan.FromMilliseconds(1));
    await Task.Delay(10);

    // Act
    var result = await cache.GetAsync<string>("key");

    // Assert
    Assert.Null(result);
}
```

### Parameterized Tests

Use `[Theory]` with `[InlineData]` for multiple scenarios:

```csharp
[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData(" ")]
public async Task SetAsync_WithInvalidKey_ThrowsArgumentException(string key)
{
    var cache = new InMemoryCacheClient();
    await Assert.ThrowsAsync<ArgumentException>(() => cache.SetAsync(key, "value"));
}
```

### Test Organization

- Mirror the main code structure (e.g., `Caching/` tests for `src/Foundatio/Caching/`)
- Use constructors and `IDisposable` for setup/teardown
- Inject `ITestOutputHelper` for test logging

### Integration Testing

- Use in-memory implementations by default
- For provider-specific tests, use test containers or stub external dependencies
- Verify data persistence and side effects
- Keep integration tests separate from unit tests

### Running Tests

```bash
# All tests
dotnet test Foundatio.slnx

# Specific test file
dotnet test --filter "FullyQualifiedName~InMemoryCacheClientTests"

# With logging
dotnet test --logger "console;verbosity=detailed"
```

## Debugging

1. **Reproduce** with minimal steps
2. **Understand** the root cause before fixing
3. **Test** the fix thoroughly
4. **Document** non-obvious fixes in code if needed

## Resilience & Reliability

- **Expect failures**: Network calls fail, resources exhaust, concurrent access races
- **Timeouts everywhere**: Never wait indefinitely; use cancellation tokens
- **Retry with backoff**: Use exponential backoff with jitter for transient failures
- **Circuit breakers**: Prevent cascading failures in distributed systems
- **Graceful degradation**: Return cached data, default values, or partial results when appropriate
- **Idempotency**: Design operations to be safely retryable
- **Resource limits**: Bound queues, caches, and buffers to prevent memory exhaustion

## Resources

- [README.md](README.md) - Overview and provider links
- [docs/](docs/) - Full documentation
- [samples/](samples/) - Usage examples
- [benchmarks/](benchmarks/) - Performance testing
