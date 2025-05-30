using Foundatio.AppHost.Extensions;
using Projects;
#pragma warning disable ASPIREPROXYENDPOINTS001

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

builder.Build().Run();
