# Azure Storage Queue Sample Applications

This folder contains sample console applications demonstrating the Azure Storage Queue implementation with both Default (envelope) and Legacy (v11 SDK compatible) modes.

## Sample Applications

### Foundatio.AzureStorage.Enqueue

Enqueues messages to an Azure Storage Queue with support for correlation IDs and custom properties.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureStorage.Enqueue --framework net8.0 -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Storage connection string (defaults to emulator)
- `-q, --queue` - Queue name (default: sample-queue)
- `-m, --message` - Message to send (default: Hello World)
- `--correlation-id` - Correlation ID for the message
- `--property` - Custom properties in key=value format (can be used multiple times)
- `--mode` - Compatibility mode: Default or Legacy (default: Default)
- `--count` - Number of messages to send (default: 1)

**Examples:**
```bash
# Send a simple message using emulator
dotnet run --project samples/Foundatio.AzureStorage.Enqueue --framework net8.0

# Send message with metadata (Default mode)
dotnet run --project samples/Foundatio.AzureStorage.Enqueue --framework net8.0 -- \
  --message "Test Message" \
  --correlation-id "12345" \
  --property "Source=Sample" \
  --property "Priority=High"

# Send message in Legacy mode (v11 SDK compatibility)
dotnet run --project samples/Foundatio.AzureStorage.Enqueue --framework net8.0 -- \
  --mode Legacy \
  --message "Legacy Message"
```

### Foundatio.AzureStorage.Dequeue

Dequeues and processes messages from an Azure Storage Queue, displaying all metadata.

**Usage:**
```bash
dotnet run --project samples/Foundatio.AzureStorage.Dequeue --framework net8.0 -- [options]
```

**Options:**
- `-c, --connection-string` - Azure Storage connection string (defaults to emulator)
- `-q, --queue` - Queue name (default: sample-queue)
- `--mode` - Compatibility mode: Default or Legacy (default: Default)
- `--count` - Number of messages to process, 0 = infinite (default: 1)

**Examples:**
```bash
# Process one message using emulator
dotnet run --project samples/Foundatio.AzureStorage.Dequeue --framework net8.0

# Process messages continuously (press Ctrl+C to stop)
dotnet run --project samples/Foundatio.AzureStorage.Dequeue --framework net8.0 -- \
  --count 0

# Process messages in Legacy mode
dotnet run --project samples/Foundatio.AzureStorage.Dequeue --framework net8.0 -- \
  --mode Legacy
```

## Azure Storage Emulator / Azurite

The sample applications default to the Azure Storage Emulator connection string for local development:

```
DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://localhost:10000/devstoreaccount1;QueueEndpoint=http://localhost:10001/devstoreaccount1;
```

### Running Azurite

To start Azurite (the Azure Storage Emulator):

```bash
# Install Azurite globally
npm install -g azurite

# Start Azurite
azurite --silent --location c:\azurite --debug c:\azurite\debug.log
```

Or using Docker:
```bash
docker run -p 10000:10000 -p 10001:10001 -p 10002:10002 mcr.microsoft.com/azure-storage/azurite
```

### Connection String Priority

The applications check for connection strings in this order:
1. `--connection-string` command line argument
2. `AZURE_STORAGE_CONNECTION_STRING` environment variable
3. Azure Storage Emulator default connection string

## Compatibility Modes

### Default Mode
- Uses message envelope format with full metadata support
- Supports `CorrelationId` and `Properties` for distributed tracing
- Recommended for new applications

### Legacy Mode
- Compatible with v11 Microsoft.Azure.Storage.Queue SDK
- Uses Base64 encoding and raw message body
- No metadata support (CorrelationId/Properties are lost)
- Use for backward compatibility with existing queues

## Demo Workflow

1. Start Azurite:
   ```bash
   azurite --silent --location c:\azurite
   ```

2. Send messages with metadata:
   ```bash
   dotnet run --project samples/Foundatio.AzureStorage.Enqueue --framework net8.0 -- \
     --message "Hello from Foundatio" \
     --correlation-id "demo-123" \
     --property "Source=Demo" \
     --property "Environment=Development" \
     --count 3
   ```

3. Process the messages:
   ```bash
   dotnet run --project samples/Foundatio.AzureStorage.Dequeue --framework net8.0 -- \
     --count 0
   ```

The dequeue application will display the full message content along with all metadata preserved by the envelope format.
