# Messaging sample

An ASP.NET host with three Aspire replicas, SQS/SNS through LocalStack, and durable jobs on Redis.

- `POST /orders` sends queued work to competing `ProcessOrderHandler` instances.
- `POST /announcements` publishes to the durable `announcements` subscription. Its replicas compete for each event. A different named subscription would receive another copy.
- `POST /reports` enqueues typed report arguments and returns a job ID; `GET /reports/{id}` shows progress.
- Global CRON schedules share one occurrence per tick; `PerNode` schedules create node-affine work for each replica.

Delivery and execution are at least once. Production business operations must tolerate retries and duplicate delivery.

[Program.cs](Program.cs) uses `AddFoundatioWorker(...)` to configure and host message consumers, jobs, schedules, and delayed dispatch together. API-only hosts use `AddFoundatio()` to register clients without starting workers.

```powershell
dotnet run --project samples/Foundatio.AppHost
```

After opening the service endpoint from the Aspire dashboard:

```powershell
$serviceUrl = 'https://localhost:<port>'
1..6 | ForEach-Object {
    Invoke-RestMethod "$serviceUrl/orders" -Method Post -ContentType application/json -Body (@{ product = 'widget'; quantity = $_ } | ConvertTo-Json)
}
Invoke-RestMethod "$serviceUrl/announcements" -Method Post -ContentType application/json -Body '{"text":"hello"}'
$job = Invoke-RestMethod "$serviceUrl/reports" -Method Post
Invoke-RestMethod "$serviceUrl/reports/$($job.jobId)"
```

For a runnable example without Redis, Docker, or AWS, use `dotnet run --project samples/Foundatio.QuickstartSample`.
