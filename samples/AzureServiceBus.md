# Azure Service Bus Sample Applications

This folder contains sample console applications demonstrating the Azure Service Bus Queue and Message Bus implementations.

## Sample Applications

### Queue Samples

#### Foundatio.AzureServiceBus.Enqueue

Enqueues messages to an Azure Service Bus Queue with support for correlation IDs and custom properties.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureServiceBus.Enqueue -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Service Bus connection string (defaults to emulator)
- `-q, --queue` - Queue name (default: foundatio-test-queue)
- `-m, --message` - Message to send (default: Hello World)
- `--correlation-id` - Correlation ID for the message
- `--property` - Custom properties in key=value format (can be used multiple times)
- `--count` - Number of messages to send (default: 1)

**Examples:**
```bash
# Send a simple message using emulator
dotnet run --project samples/Foundatio.AzureServiceBus.Enqueue

# Send message with metadata
dotnet run --project samples/Foundatio.AzureServiceBus.Enqueue -- \
  --message "Test Message" \
  --correlation-id "12345" \
  --property "Source=Sample" \
  --property "Priority=High"

# Send multiple messages
dotnet run --project samples/Foundatio.AzureServiceBus.Enqueue -- \
  --message "Batch Message" \
  --count 5
```

#### Foundatio.AzureServiceBus.Dequeue

Dequeues and processes messages from an Azure Service Bus Queue.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureServiceBus.Dequeue -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Service Bus connection string (defaults to emulator)
- `-q, --queue` - Queue name (default: foundatio-test-queue)
- `--count` - Number of messages to process, 0 = infinite (default: 1)

**Examples:**
```bash
# Process one message using emulator
dotnet run --project samples/Foundatio.AzureServiceBus.Dequeue

# Process messages continuously (press Ctrl+C to stop)
dotnet run --project samples/Foundatio.AzureServiceBus.Dequeue -- --count 0
```

### Message Bus Samples

#### Foundatio.AzureServiceBus.Publish

Publishes events to an Azure Service Bus Topic.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureServiceBus.Publish -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Service Bus connection string (defaults to emulator)
- `-t, --topic` - Topic name (default: test-messages)
- `-e, --event-type` - Event type (default: SampleEvent)
- `-d, --data` - Event data (default: Hello World)
- `--correlation-id` - Correlation ID for the message
- `--count` - Number of messages to publish (default: 1)

**Examples:**
```bash
# Publish a simple event using emulator
dotnet run --project samples/Foundatio.AzureServiceBus.Publish

# Publish event with correlation ID
dotnet run --project samples/Foundatio.AzureServiceBus.Publish -- \
  --event-type "OrderCreated" \
  --data "Order #12345" \
  --correlation-id "order-12345"
```

#### Foundatio.AzureServiceBus.Subscribe

Subscribes to events from an Azure Service Bus Topic.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureServiceBus.Subscribe -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Service Bus connection string (defaults to emulator)
- `-t, --topic` - Topic name (default: test-messages)
- `-s, --subscription` - Subscription name (default: test-subscription)

**Examples:**
```bash
# Subscribe to events using emulator
dotnet run --project samples/Foundatio.AzureServiceBus.Subscribe

# Subscribe with a specific subscription name
dotnet run --project samples/Foundatio.AzureServiceBus.Subscribe -- \
  --subscription "my-subscription"
```

## Azure Service Bus Emulator

The sample applications default to the Azure Service Bus Emulator connection string for local development.

### Running the Emulator

Start the emulator using Docker Compose from the repository root:

```bash
docker-compose up -d
```

### Connection String Priority

The applications check for connection strings in this order:
1. `--connection-string` command line argument
2. `AZURE_SERVICEBUS_CONNECTION_STRING` environment variable
3. Azure Service Bus Emulator default connection string

## Demo Workflow

### Queue Demo

1. Start the emulator:
   ```bash
   docker-compose up -d
   ```

2. In one terminal, start the dequeue process:
   ```bash
   dotnet run --project samples/Foundatio.AzureServiceBus.Dequeue -- --count 0
   ```

3. In another terminal, send messages:
   ```bash
   dotnet run --project samples/Foundatio.AzureServiceBus.Enqueue -- \
     --message "Hello from Foundatio" \
     --correlation-id "demo-123" \
     --property "Source=Demo" \
     --count 3
   ```

### Message Bus Demo

1. Start the emulator:
   ```bash
   docker-compose up -d
   ```

2. In one terminal, start the subscriber:
   ```bash
   dotnet run --project samples/Foundatio.AzureServiceBus.Subscribe
   ```

3. In another terminal, publish events:
   ```bash
   dotnet run --project samples/Foundatio.AzureServiceBus.Publish -- \
     --event-type "UserCreated" \
     --data "User john@example.com" \
     --count 3
   ```
