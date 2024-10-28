using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Foundatio_HostingSample>("HostingSample")
    .WithExternalHttpEndpoints()
    .WithArgs("all");

builder.Build().Run();
