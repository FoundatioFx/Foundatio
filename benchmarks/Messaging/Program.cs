using Foundatio.Messaging.Benchmarks;

if (args.Contains("--help"))
{
    Console.WriteLine("Messaging load benchmark: --engine foundatio|masstransit|loopback --transport memory|redis|sqs --scenario queue|pubsub --seconds 15 --warmup 3 --producers 32 --consumers 32 --prefetch 32 --subscribers 4 --payload 1024 --batch 1 --rate 0 --outstanding 4096 --output result.json");
    Console.WriteLine("Connections: PERF_REDIS (localhost:16379), PERF_AWS_MODE=localstack|live (defaults to localstack), PERF_AWS_URL (http://localhost:24566; ignored in live mode), PERF_AWS_REGION (us-east-1). Live AWS uses the SDK credential chain. Each run creates and removes uniquely named queues/topics.");
    return 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try { return await BenchmarkRunner.RunAsync(BenchmarkOptions.Parse(args), cancellation.Token); }
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
