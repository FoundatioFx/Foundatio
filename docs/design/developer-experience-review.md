# Developer experience review

Scope: the unreleased messaging and durable job redesign, including setup, common operations, lifecycle, registration errors, provider configuration, test helpers, and the first-run documentation.

| Journey | Friction found | Decision |
| --- | --- | --- |
| Run a normal worker | Four unrelated hosting calls in the first example | `AddFoundatioWorker(configure)` configures and hosts the required roles in one explicit call. Keep individual host APIs for split deployments. |
| Run a producer API | Registering a client must not start workers | Keep `AddFoundatio()` inert. Document the producer/worker distinction first. |
| Subscribe durably | Omitting a name silently selected temporary behavior | Require a durable name in `AddSubscriber`; expose `AddTemporarySubscriber` explicitly. |
| Register a handler | Invalid concurrency, attempts, acknowledgement, or destinations failed late | Validate receiving options at registration as well as direct subscription. |
| Register a schedule | Invalid options or an abstract job survived until startup/execution | Validate complete schedule options and concrete job types before adding registrations. |
| Configure Redis | A second explicit connection string was silently ignored | Reject conflicting settings before connecting; allow one explicit setting after a default registration. |
| Test a queue of jobs | `RunAllQueuedAsync` stopped at 100 | Drain ready work across batches with a timeout. Delayed retries remain queued. |
| Test one job | `RunToCompletionAsync(handle)` executed unrelated jobs | Execute only the requested handle. |
| Learn the library | Operational internals dominated the introductory examples | Lead with one complete worker, then sending work, publishing events, and optional typed jobs. Put split hosting and transport administration later. |

Keep the delivery model small: send queued work, publish events, and add durable jobs only for tracked execution or schedules. Keep `IJob<TArgs>` and its two generic enqueue parameters because they enforce the argument contract at compile time. Keep explicit receipts, claim tokens, and schedule revisions in their advanced contracts; ordinary handlers and jobs do not need to manage them.

Provider delivery guarantees, idempotency, and schema compatibility remain explicit. Simplifying setup does not change at-least-once execution into exactly-once side effects.

## Intentional breaking changes

- `AddSubscriber` now requires a nonblank durable name. Replace unnamed DI registrations with `AddTemporarySubscriber`. Dynamic `SubscribeAsync` retains its options-based lifetime selection.
- Invalid receiving/schedule options and non-concrete job types fail during registration.
- Conflicting Redis connection strings throw instead of silently using the first connection. A pre-registered multiplexer requires omitting `connectionString`.
- `RunToCompletionAsync(handle)` leaves unrelated jobs queued. `RunAllQueuedAsync()` drains more than one batch and has a 30-second safety timeout.

`AddFoundatioWorker` is additive. The individual role hosts remain useful for separate worker, scheduler, and dispatcher processes. Its callback is the configuration boundary: put worker registrations there or register them before calling it.

## Design choices retained

- Queue commands and event subscriptions remain separate receiving APIs, even though they share a bus and handler interface. This makes competing consumption versus fan-out visible at registration.
- Handlers receive `IMessageContext<T>` so metadata, cancellation, and optional settlement are available without a second handler abstraction. Ordinary handlers read `Message` and return a task.
- Provider capabilities stay explicit. Temporary subscriptions, durable storage, ordering, and dead-letter inspection cannot be made identical by a convenience API.
- Stable wire names and durable subscription names remain deliberate choices. Changing CLR names or replica counts should not silently change persisted contracts.
- Job arguments retain compile-time constraints. Removing the second generic enqueue parameter would sacrifice that guarantee or require another binding abstraction.

The README, introductory pages, primary guides, serializer examples, runnable samples, and repository skill now use the same vocabulary and current APIs.

## Validation

- `Foundatio.slnx` builds with zero warnings and zero errors.
- Full solution tests with disposable Redis 8.6 and LocalStack 3.8.1: 2,065 total; 2,042 passed, 23 skipped, zero failed.
- Focused regressions cover combined and single-feature worker startup, missing dependencies, temporary subscriptions, registration validation, conflicting Redis settings, draining 201 jobs, and executing one job without consuming another.
- Documentation site builds successfully. The quickstart processes a command, an event, a typed job with progress, and CRON cleanup; intentional shutdown completes cleanly.
- Whitespace formatting and `git diff --check` pass. No removed `InMemoryQueue` or `JobBase` examples remain in the README or documentation.
- The broader `Foundatio.All.slnx` workspace remains unavailable because sibling provider checkouts are absent. Validation covers all projects in this repository's `Foundatio.slnx`.
