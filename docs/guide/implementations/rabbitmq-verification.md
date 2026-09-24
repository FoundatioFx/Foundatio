---
title: RabbitMQ Verification
---

# RabbitMQ 4.2.5 verification

This contributor guide is maintained in **FoundatioFx/Foundatio**, but the commands below run from the **Foundatio.RabbitMQ repository root**. In the [provider review stack](./rabbitmq.md), [#106](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/106) owns the quorum priority guard, [#104](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/104) owns broker/TLS infrastructure, [#105](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/105) owns delivery/recovery behavior, and [#100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100) adds the interactive sample and documentation. A documentation branch or test definition is not proof that an implementation is released. Aggregate implementation reference: [`62f87cc`](https://github.com/FoundatioFx/Foundatio.RabbitMQ/tree/62f87ccecbe8ff3b7f34af31692c9fd2ada841a1).

All provider-managed brokers use `rabbitmq:4.2.5-management`: Compose, Aspire primary/chaos nodes, the delayed-plugin base, and both TLS brokers. The plugin artifact is independently versioned `4.2.0`. No 4.3 upgrade is included.

## Run the existing Build

Use the existing shared **Build** workflow and the same solution locally. Do not create separate endpoint, integration, TLS, or package-verification workflows.

```bash
# Working directory: your checkout of FoundatioFx/Foundatio.RabbitMQ

docker build -t foundatiorabbitmq-rabbitmq-delayed:latest build
dotnet build Foundatio.RabbitMQ.slnx --configuration Release
FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true \
  dotnet test --solution Foundatio.RabbitMQ.slnx --configuration Release --no-build
```

Docker must be available. Building the image installs the existing plugin; Aspire provisions the test brokers. TLS cases are ordinary facts/theories in the normal run, not explicit cases selected by another workflow. The existing publishing configuration, including Feedz, is unchanged by documentation work.

For a local individual-result report, use the built xUnit executable instead of repeating the MTP run:

```bash
mkdir -p TestResults
FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true \
  dotnet tests/Foundatio.RabbitMQ.Tests/bin/Release/net10.0/Foundatio.RabbitMQ.Tests.dll \
  -failSkips -result-ctrf TestResults/rabbitmq.json
```

Native xUnit switches differ from `dotnet test`/Microsoft.Testing.Platform switches. Do not mix them. A help invocation, empty selection, failed fixture, or skipped required test is not successful behavioral verification.

## Run the interactive Aspire sample

From the provider repository, build the delayed-plugin image and solution as above, then start the shared AppHost:

```bash
dotnet run --project tests/Foundatio.RabbitMQ.AppHost --configuration Release \
  --no-build --launch-profile http
```

The HTTP launch profile is for the local development dashboard; use the URL emitted by Aspire. `--no-build` uses the completed solution build instead of selecting a sibling Foundatio source checkout through project-level build defaults. The sample starts one publisher and two independent subscriptions on `sample-topic`:

| Resource | Source queue | Quarantine exchange / queue |
|---|---|---|
| `subscriber-classic` | `sample-classic-orderevent` | `sample-classic-quarantine` / `sample-classic-quarantine-queue` |
| `subscriber-quorum` | `sample-quorum-orderevent` | `sample-quorum-quarantine` / `sample-quorum-quarantine-queue` |

Both subscribers use durable, nonexclusive, non-autodelete queues, Automatic acknowledgement, strict dispatch, prefetch 10, and application delivery limit 2. The sample explicitly provisions each direct quarantine exchange/queue with routing key `quarantine`; that is sample setup, not automatic provider provisioning. Quarantine setup uses the same `--hosts` / `RABBITMQ_HOSTS` replacement endpoints as the bus, while credentials and virtual host come from the connection URI. Both source and quarantine queues default to a 16 MiB ready-message byte limit and reject-publish overflow. `--max-length-bytes` changes the sample cap. Quorum additionally selects at-least-once broker DLX with a finite broker delivery limit.

The publisher uses `--publisher-confirms --durable --require-routing`; each subscriber uses `--fail-every 5` to permanently fail every fifth order. Inspect actual consumer readiness before evaluating results: a running Aspire resource is not proof that its subscription is established. Confirm normal orders in both subscriber logs and failed orders in both quarantine queues, preserving event IDs. Classic retry remains local to its subscription; quorum retries through broker requeue. A publish confirmation cannot confirm that both logical subscriptions exist or have processed an event. Failed sample publications are logged and **not replayed**; this sample is not a durable outbox or a complete loss-reconciliation test.

Use dashboard commands on the sample's chaos brokers to trigger disk/memory alarms, restore limits, or close connections. Commands target exact per-run container names. Alarm triggers capture the previous effective limit in bytes; restore commands restore that captured value. Each Docker invocation has a 30-second timeout. These commands deliberately disrupt only the sample environment; they do not test broker-storage loss or the separate TLS fixture.

Before a drill, record queue types, policies, counts, consumer state, and selected event IDs. After restoring limits or connections, inspect actual alarms and confirm new publications, retained work, and quarantine transfer resume. A command reporting success alone is insufficient evidence. Keep original containers and the AppHost alive until restore is verified; captured limits belong to that run. Full shared-suite execution, including TLS, is still required after an interactive sample check.

## Test conventions and ownership

Provider contract tests inherit `RabbitMqMessageBusTestBase` / `RabbitMqMessageBusClassicTestBase`, which use Foundatio's shared `MessageBusTestBase`. Preserve inherited contract names and signatures. Shared behavior must use the virtual `GetMessageBus` factory so classic, quorum, and delayed-exchange subclasses exercise their own configuration. The priority verifier belongs in the shared base, not a concrete test class; the separate builder/direct-options matrix reuses it rather than duplicating its assertions.

Focused tests use `TestWithLoggingBase`, `ITestOutputHelper`, inherited `TestCancellationToken`, and `Log` as the logger factory. Use structured `_logger` templates rather than interpolated text hidden inside a generic log field. Extend an existing relevant test class before adding another one. Configuration-only constructor guards belong in `RabbitMqMessageBusOptionsTests`, not a broker-dependent fixture.

Name new standalone cases `Operation_State_ExpectedOutcome`, with an `Async` suffix for task-returning methods, for example `SubscribeAsync_WithFailedInitialization_RemovesRegistrationAndAllowsRetryAsync`. Keep test methods alphabetical within each class, with explicit Arrange/Act/Assert sections; put helper methods and nested test types after the cases. Alphabetical source order does not impose runtime order: do not add a test-case orderer or make a test depend on another test's side effects. Parameterized cases retain their full argument matrix. Ordering tests must compare the actual received sequence, not sort it to manufacture a match.

One `RabbitMqTestCollection` owns `AspireFixture`. Broker-dependent classes join that collection; configuration-only classes remain independent. The fixture disposes the built `DistributedApplication` first, then the testing builder, then test certificates, including on partially successful startup. Clearing owned references makes repeated disposal safe. Serializing tests that mutate shared broker resources is not an execution-order contract between cases.

Infrastructure is required when `CI` or `GITHUB_ACTIONS` is `true` or `1`, or with `FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true`. A false local override cannot weaken CI. In an optional local run, a TLS certificate trust-store denial skips only TLS-dependent cases while the other brokers can run. Required runs still fail when TLS infrastructure is unavailable. Optional skips are not full-suite verification.

Live version tests assert 4.2.5 for the primary, delayed, three chaos, and trusted TLS brokers. Successful TLS traffic cases also check it. The untrusted TLS broker has the same exact image declaration; certificate validation is not bypassed merely to query its version. Version tags are compatibility pins, not immutable digests or security certification.

## Temporary TLS infrastructure

See [endpoint identity and port rules](./rabbitmq.md#tls-and-endpoints) for the production contract.

The fixture generates two independent short-lived certificate authorities and server certificates, writing temporary certificate and broker configuration files asynchronously with startup cancellation. Only one public CA is added to the **current user's** root store and that exact certificate is removed during disposal. No system-wide trust change or `sudo` is required. Temporary server keys live in a private test directory, are mounted read-only into test containers, and are removed after the brokers stop. CA private keys are not written to disk.

Both brokers disable plaintext AMQP. The trusted certificate covers `localhost`, `127.0.0.1`, and `::1`, but not `127.0.0.2`. Strict probes distinguish wrong identity from an untrusted chain, with healthy controls before and after rejection. Timeout or connection refusal does not pass those assertions.

Four cases test custom-port traffic over IPv4/IPv6 with URI-only and replacement-host configurations. Two established-session cases observe actual publisher/subscriber shutdown, retain an identified message during path loss, restore only an alternative endpoint, and verify pending and new post-recovery IDs. The TCP relays forward opaque bytes without terminating TLS. This proves endpoint-path recovery with shared broker state, not broker-storage-loss resilience.

## Delivery and lifecycle coverage

| Area | Required assertion |
|---|---|
| Failed/ambiguous handoff | Original subscriber remains open and ACK-capable; original identity remains recoverable. Caller-observed failure after real broker acceptance accounts for duplicate copies. |
| Subscription-local retry | Only the failed logical subscription retries; an independent successful subscription is not rebroadcast to. Retry/terminal copies clear `CC`, `BCC`, and publisher `UserId` while preserving payload and logical identity. |
| Terminal route | For classic and quorum, configured-but-missing, unbound, and full destinations retain work with unhealthy diagnostics until repair. Permissive Automatic no-destination retention and explicit discard are distinct outcomes. Invalid retry metadata does not reset a budget. |
| Broker dead-lettering | A separate quorum case checks its finite broker limit and at-least-once transfer with an initially unavailable route. Do not substitute client republishing for this test. |
| Required dispatch | Constructor guards reject non-Automatic mode, discard, and null/empty/whitespace typed terminal exchanges, including a raw-only DLX. Unexpected malformed/unsupported/unmatched typed delivery uses the terminal policy. Cancelling one local subscription does not block another live handler. |
| Capacity and permissions | A full classic source retains a failed retry until capacity returns; a full quarantine blocks transfer on either type. Restricted-role checks cover default-exchange write for classic retries and terminal-exchange write. Ready-message limits do not assert total disk bounds. |
| Cancellation | Distinguish a handler-local timeout from subscription cancellation and transport shutdown. Exercise retry/terminal outcomes under Automatic acknowledgements, permissive-mode controls, cancelled setup callers with another pending subscriber, and last-subscriber removal/resubscription. |
| Lifecycle | Channel-only closure, consumer cancellation, failed initialization, queue recreation, stale callbacks, and uncooperative-handler shutdown include actual receipt/settlement assertions. Exercise legacy subscription-removal hooks and busy transport locks; distinguish bounded disposal waits from eventual cleanup. |
| Delayed publication | Required broker scheduling rejects memory fallback. Kill a test publisher after confirmed scheduling and before the due time; the broker must later deliver the same ID. |
| Prefetch/restarts | Inspect backlog while ACKs are withheld and reconcile every required ID after recovery, rather than permitting percentage loss. |
| Sample provisioning | Launch the subscriber process with an unavailable URI endpoint and a healthy replacement host. Verify quarantine setup and subscription readiness; a helper-only endpoint test is insufficient. |
| Header culture | Convert numeric AMQP headers under a non-English culture such as `fr-FR` and assert invariant property strings; byte-array headers remain UTF-8 text. |
| Priority on 4.2.5 | Classic cases exercise configured numeric priority; quorum cases exercise normal/high tiers without `x-max-priority`. Cover builder and direct options, omitted/zero priority, and prefetch effects. Do not assert later-broker semantics. |

Handoff ambiguity is injected at the caller boundary around a real broker publication, not claimed as packet-level confirmation loss. Publisher-process termination does not establish replicated scheduling. Tests allow the real 4.2.5 dead-letter retry timer rather than changing it solely to hide a slow result.

Do not run these tests against production. They alter test alarms, close channels, interrupt test TCP paths, and kill a test-owned process. That process receives credentials through its environment, not command-line arguments.

## Evidence and acceptance

Record the actual revision, dependency versions, broker versions, commands, complete test counts, and review findings in the provider PR. Require TLS and process-failure cases to execute. Confirm the exact final candidate passed and the base branch is current. Implementation, merge, release, and deployment are separate states.

For a test-only refactor, reconcile the original and resulting case inventories so renames or moves cannot conceal lost coverage. For a defect regression, run it against the unchanged runtime first and verify the intended behavioral failure, then rerun the same case against the repair. A missing fixture, compiler error, or empty selection is not defect reproduction. Keep a newly added but unexecuted case distinct from a passing regression.

Use [delivery-safety/adoption guidance](./rabbitmq-delivery-safety.md) for topology, terminal handling, and operational prerequisites. Tests execute on .NET 10/Linux in the reviewed setup; .NET 8 compilation is not its own broker-runtime matrix. Packaging/publication skipped by a PR build is not package validation. A passing test count does not prove every legacy chaos assertion or every production failure model.

This page describes how to verify; revision-specific results remain in the [provider review stack](./rabbitmq.md) and [issue #99](https://github.com/FoundatioFx/Foundatio.RabbitMQ/issues/99), not as permanent guarantees in the guide. Results from an earlier aggregate revision do not verify the current stack heads.
