using System;
using Foundatio.Serializer;

namespace Foundatio.Jobs;

internal sealed record ScheduledJobRegistration(Type JobType, string Cron, CronJobOptions Options, object? Arguments)
{
    public string Name => Options.Name ?? ScheduledJobDefinition.DefaultNameFor(JobType);

    public void Validate()
    {
        JobArgumentContract.Validate(JobType, Arguments);
        new ScheduledJobDefinition
        {
            Name = Name,
            Cron = Cron,
            JobType = JobType.FullName ?? JobType.Name,
            TimeZoneId = Options.TimeZone?.Id ?? "UTC",
            Scope = Options.Scope,
            Overlap = Options.Overlap,
            MisfireWindow = Options.MisfireWindow,
            MaxAttempts = Options.MaxAttempts,
            RetryPolicy = Options.RetryPolicy,
            UnclaimedLifetime = Options.UnclaimedLifetime,
            ConfigurationVersion = Options.ConfigurationVersion
        }.Validate();
    }

    public ScheduledJobDefinition Create(IJobTypeRegistry jobTypes, ISerializer serializer)
    {
        JobArgumentContract.Validate(JobType, Arguments);
        var definition = new ScheduledJobDefinition
        {
            Name = Name,
            Cron = Cron,
            JobType = jobTypes.GetName(JobType),
            TimeZoneId = Options.TimeZone?.Id ?? "UTC",
            Scope = Options.Scope,
            Overlap = Options.Overlap,
            MisfireWindow = Options.MisfireWindow,
            MaxAttempts = Options.MaxAttempts,
            RetryPolicy = Options.RetryPolicy,
            UnclaimedLifetime = Options.UnclaimedLifetime,
            Enabled = Options.Enabled,
            ConfigurationVersion = Options.ConfigurationVersion,
            Payload = Arguments is null ? null : (ReadOnlyMemory<byte>?)serializer.SerializeToBytes(Arguments),
            PayloadType = Arguments?.GetType().FullName
        };
        definition.Validate();
        return definition;
    }
}
