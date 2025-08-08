# Copilot AI Coding Agent Instructions for Foundatio

## Key Principles

All contributions must respect existing formatting and conventions specified in the `.editorconfig` file. You are a distinguished engineer and are expected to deliver high-quality code that adheres to the guidelines in the instruction files.

Let's keep pushing for clarity, usability, and excellence—both in code and user experience.

**See also:**
- [General Coding Guidelines](instructions/general.instructions.md)
- [Testing Guidelines](instructions/testing.instructions.md)

## Project Overview
Foundatio is a modular .NET library providing pluggable building blocks for distributed applications, including Caching, Queues, Locks, Messaging, Jobs, File Storage, and Resilience. It is designed for extensibility, testability, and easy swapping of implementations (e.g., Redis, Azure, AWS, in-memory).

## Key Architecture & Patterns
- **Core abstractions** are in `src/Foundatio/` (e.g., `ICacheClient`, `IQueue`, `IMessageBus`).
- **Implementations** are provided in separate projects (see [README.md](../README.md) for external providers).
- **Dependency Injection** is used throughout; extension methods for DI are in `FoundatioServicesExtensions.cs`.
- **Service boundaries**: Each major feature (caching, queues, etc.) is a separate directory and interface.
- **Cross-cutting concerns** (e.g., resilience, metrics) are modular and can be added via DI.
- **Sample apps** are in `samples/` and demonstrate integration patterns.

## Developer Workflows
- **Build**: Use `dotnet build Foundatio.slnx` or VS Code tasks (`build`).
- **Test**: Use `dotnet test Foundatio.slnx` or VS Code tasks (`test`).
- **Pack**: Use `dotnet pack -c Release -o ./artifacts` or VS Code task (`pack`).
- **Start/Stop all services**: Use `start-all-services.ps1` and `stop-all-services.ps1` for local dev environments.
- **Benchmarks**: See `benchmarks/` for performance tests.

## Project-Specific Conventions
- **All core logic is in `src/Foundatio/`**; avoid adding new features to sample or test projects.
- **Prefer interface-based design** for extensibility and testability.
- **Use in-memory implementations for tests and local dev**; avoid external dependencies in unit tests.
- **Follow existing naming conventions**: `Foundatio.[Feature]` for projects, `I[Feature]` for interfaces.
- **Macros and parsing**: See `CronExpression.cs` for custom cron parsing logic and macro support.

## Integration & External Dependencies
- **External providers** (Redis, Azure, AWS, etc.) are in separate repos/packages; see README for links.
- **NuGet** is used for dependency management; see `NuGet.config` and project files.
- **Metrics, logging, and resilience** are optional and can be added via DI.

## References
- [README.md](../README.md) for high-level overview and links to provider repos
- `src/Foundatio/` for core abstractions and patterns
- `samples/` for usage examples
- `benchmarks/` for performance testing

---

If you are unsure about a pattern or workflow, check the README or look for similar implementations in `src/Foundatio/`. When in doubt, prefer extensibility and testability.
