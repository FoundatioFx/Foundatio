using Foundatio.AppHost.Extensions;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("Redis", port: 6379)
    .WithContainerName("Foundatio-Redis")
    .WithImageTag(ImageTags.Redis)
    .WithEndpointProxySupport(false)
    .WithClearCommand()
    .WithRedisInsight(b => b.WithEndpointProxySupport(false).WithContainerName("Foundatio-RedisInsight")
        .WithUrlForEndpoint("http", u => u.DisplayText = "Cache"));

builder.AddProject<Foundatio_HostingSample>("Foundatio-HostingSample")
    .WithExternalHttpEndpoints()
    .WithReplicas(3)
    .WithReference(cache)
    .WaitFor(cache)
    .WithArgs("all")
    .WithUrls(u =>
    {
        u.Urls.Clear();
        u.Urls.Add(new ResourceUrlAnnotation { Url = "/jobs/status", DisplayText = "Job Status", Endpoint = u.GetEndpoint("http") });
        u.Urls.Add(new ResourceUrlAnnotation { Url = "/jobs/run", DisplayText = "Run Job", Endpoint = u.GetEndpoint("http") });
    });

// LocalStack provides AWS SQS/SNS locally so the messaging sample's AWS transport works with no cloud account.
var localstack = builder.AddContainer("localstack", "localstack/localstack", "3")
    .WithContainerName("Foundatio-LocalStack")
    .WithEnvironment("SERVICES", "sqs,sns")
    .WithEndpoint(port: 4566, targetPort: 4566, scheme: "http", name: "gateway")
    .WithHttpHealthCheck("/_localstack/health", endpointName: "gateway");

// The redesigned messaging + durable-jobs sample, scaled to 3 replicas so you can watch the queue load-balance across
// instances, the pub/sub topic fan out to every instance, and durable/CRON jobs get claimed by a single instance.
// Messaging runs on AWS (SQS/SNS via LocalStack) and durable jobs on Redis; set Messaging__Provider=Redis to run the
// messaging on Redis Streams instead.
builder.AddProject<Foundatio_MessagingSample>("Foundatio-MessagingSample")
    .WithExternalHttpEndpoints()
    .WithReplicas(3)
    .WithReference(cache)
    .WaitFor(cache)
    .WaitFor(localstack)
    .WithEnvironment("Messaging__Provider", "Aws")
    .WithEnvironment("Aws__ServiceUrl", localstack.GetEndpoint("gateway"));

await builder.Build().RunAsync();
