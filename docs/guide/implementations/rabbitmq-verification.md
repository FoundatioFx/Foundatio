# RabbitMQ verification

This contributor guide belongs to the central Foundatio documentation. Commands run from a checkout of **Foundatio.RabbitMQ**, not the Foundatio core repository.

::: warning Companion implementation branch
These instructions describe [Foundatio.RabbitMQ PR #100](https://github.com/FoundatioFx/Foundatio.RabbitMQ/pull/100), branch `fix/99-tls-and-verification-foundation`, reviewed at `7c1d47778779cbebd111efe0a6686721488c618d`. They are not instructions for an older provider release. CI results, exact tested revisions, and review decisions belong in that PR and [issue #99](https://github.com/FoundatioFx/Foundatio.RabbitMQ/issues/99), not permanent test-count claims in this guide.
:::

## Broker baseline and normal Build

All repository-managed broker declarations use **`rabbitmq:4.2.5-management`**: Compose, the Aspire primary and chaos nodes, the delayed-plugin image base, and both TLS brokers. No 4.3 upgrade is included. The plugin artifact is independently versioned 4.2.0. Exact version tags are compatibility pins, not immutable digests or a security certification.

Use the provider's existing shared **Build** workflow and the same solution locally. There are no separate endpoint, integration, TLS, or package-verification workflows.

```bash
# Run from the Foundatio.RabbitMQ repository root.
docker build -t foundatiorabbitmq-rabbitmq-delayed:latest build
dotnet build Foundatio.RabbitMQ.slnx --configuration Release
FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true \
  dotnet test --solution Foundatio.RabbitMQ.slnx --configuration Release --no-build
```

Docker must be running and the .NET SDK required by the checkout must be installed. The image build installs the existing delayed-exchange plugin; Aspire provisions the actual brokers. TLS tests are normal facts/theories with fixture-owned certificates, not explicit exclusions. The Build's one provider-specific preparation input builds the delayed-plugin image. Publishing configuration, including Feedz, remains unchanged.

For individual local execution records, use the built native xUnit executable instead of repeating the Microsoft.Testing.Platform run:

```bash
mkdir -p TestResults
FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true \
  dotnet tests/Foundatio.RabbitMQ.Tests/bin/Release/net10.0/Foundatio.RabbitMQ.Tests.dll \
  -failSkips -result-ctrf TestResults/rabbitmq.json
```

Native xUnit and `dotnet test`/Microsoft.Testing.Platform use different switches. Do not combine them. Empty selections, help output, skipped cases, and failed fixtures are not passing behavioral evidence. Running the tests does not publish a package.

## Test conventions and ownership

Provider contract tests retain `RabbitMqMessageBusTestBase` / `RabbitMqMessageBusClassicTestBase` and Foundatio's shared `MessageBusTestBase`. Focused provider tests use `TestWithLoggingBase`, `ITestOutputHelper`, inherited `TestCancellationToken`, and the test logger factory.

One `RabbitMqTestCollection` owns `AspireFixture`. Broker-dependent classes join that collection; fast configuration classes remain outside it. Collection disposal stops its application and removes test-owned certificates/files rather than leaving a static application indefinitely.

Infrastructure is required when `CI` or `GITHUB_ACTIONS` is `true` or `1`, or when `FOUNDATIO_RABBITMQ_REQUIRE_INFRASTRUCTURE=true`. A false local override cannot weaken CI. Without that requirement, optional local infrastructure failures may skip dependent tests; do not treat those skips as verification.

Live version assertions check 4.2.5 on the primary, delayed, all three chaos nodes, and trusted TLS broker. Positive TLS traffic cases check the version too. Both TLS brokers have the same pinned image; the untrusted broker is used for certificate rejection, not an authenticated probe that bypasses validation.

## TLS tests

The fixture generates two independent short-lived certificate authorities and server certificates. Only one public CA is installed in the **current user's** root store; its exact certificate is removed during disposal. No system-wide trust change or sudo is required. Temporary server keys are kept in a private test directory, mounted read-only in test containers, and removed after shutdown. CA private keys are not persisted.

The brokers disable plaintext AMQP. The trusted certificate covers `localhost`, `127.0.0.1`, and `::1`, but not `127.0.0.2`. Negative tests distinguish wrong identity from an untrusted chain, with healthy controls before/after; connection refusal or timeout does not satisfy rejection assertions.

Positive cases exercise URI-only/replacement endpoints, custom ports, IPv4/IPv6, and actual identified message receipt. Established-session cases interrupt the observed publisher/subscriber paths, retain pending work while both paths are down, restore only the target's alternative endpoint, and reconcile pending and post-recovery IDs. Shared TCP relays forward opaque bytes without terminating TLS. This proves endpoint-path recovery against shared broker state, not survival of permanent broker storage loss.

## Delivery and lifecycle assertions

| Area | Required observation |
|------|----------------------|
| Failed/ambiguous transfer | Keep the original subscriber open and ACK-capable. Failure must retain the source; reported failure after a real confirmed publication may leave duplicate copies with stable identity. |
| Local retries | A successful independent subscription receives one copy; only the failed queue receives deliberate retries. |
| Terminal outcomes | Missing, unbound, or full destinations retain work until repair. No-destination retention and explicit discard are separate cases. Invalid retry metadata cannot reset the budget. |
| Broker dead-lettering | Exercise the quorum broker's own finite budget and at-least-once transfer separately from client terminal publication, including an initially unavailable route. |
| Required dispatch | Malformed/unknown/unmatched typed deliveries reach a terminal outcome rather than a successful skipped dispatch; local cancellation does not block another live handler. |
| Lifecycle | Separate channel-only closure, consumer cancellation, initialization rollback, queue recreation for new work, late completions, and uncooperative-handler shutdown. Verify actual receipt/settlement, not just TCP health. |
| Delayed delivery | Required broker delay rejects memory fallback. Kill a test child publisher after confirmed scheduling and before the due time; require later receipt of the same ID. |
| Flow control/restart | Verify broker backlog while ACKs are withheld and drain every expected ID. Reconcile confirmed publication IDs across rolling restarts rather than tolerating a percentage loss. |

Handoff ambiguity is injected at the caller boundary around real broker publication; it is not packet-level confirmation loss. Publisher-process termination does not prove replication of the plugin's single-node storage. Allow the real 4.2.5 dead-letter retry timer to run rather than changing broker settings merely to shorten a test.

Do not point these tests at production: they close channels, interrupt TCP paths, change test alarms, and terminate a test-owned publisher. The process helper receives credentials through its environment, not command-line arguments.

## Acceptance

Record candidate SHA, resolved dependency graph, broker versions, build result, executed/pass/fail/skip totals, and relevant message-ID evidence in the provider PR. Every selected case, including TLS/process tests, must execute before acceptance. Source inspection, implemented tests, successful CI, package publication, and deployed-application validation are different states.

The provider builds for net8.0/net10.0, but these broker tests run on net10.0; compilation alone is not a net8.0 runtime matrix. Preserve existing publishing behavior and do not introduce extra workflows to report the same suite. The [implementation guide](rabbitmq.md) defines required profiles, retention/replay decisions, broker-budget prerequisites, and deployment limitations.
