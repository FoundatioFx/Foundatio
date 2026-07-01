# Foundatio.Aws.Tests

Runs the shared transport conformance suite (`MessageTransportConformanceTests`) against the AWS SQS/SNS
`IMessageTransport` (`Foundatio.Aws`). This is a temporary in-repo provider used to validate the redesigned transport
contract against a real broker before it is extracted to its own package.

## Run against LocalStack

```sh
# 1. Start LocalStack (SQS + SNS)
docker compose -f tests/Foundatio.Aws.Tests/docker-compose.yml up -d

# 2. Point the tests at it
export FOUNDATIO_AWS_CONNECTION_STRING="serviceurl=http://localhost:4566;accesskey=test;secretkey=test;region=us-east-1"

# 3. Run the conformance suite
dotnet test tests/Foundatio.Aws.Tests/Foundatio.Aws.Tests.csproj
```

When `FOUNDATIO_AWS_CONNECTION_STRING` is **not** set, every test is skipped (so the project is safe in CI without a broker).

To run against real AWS, set the connection string to real credentials/region (omit `serviceurl`), e.g.
`accesskey=...;secretkey=...;region=us-east-1`.

## Capability coverage

SQS/SNS supports pull receive, visibility timeout, lock renewal, redelivery delay (12h cap), delayed delivery (15-min
cap), provisioning, and stats. It does **not** support per-message priority, per-message TTL/expiration, push delivery,
or transport-native dead-lettering (the core owns retry/dead-lettering). Conformance tests for those capabilities skip
automatically via their `ISupports*` checks.

Each run uses a unique `ResourcePrefix` so leftover messages from a prior run cannot leak in. LocalStack state is
ephemeral; restart the container to reset.
