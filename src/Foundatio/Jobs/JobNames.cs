namespace Foundatio.Jobs;

/// <summary>The names Foundatio gives jobs.</summary>
public static class JobNames
{
    /// <summary>
    /// The job name of <typeparamref name="TJob"/>, which is also the default <see cref="CronJobOptions.Name"/> of its
    /// schedule: the <see cref="JobAttribute.Name"/> declared on the type, or its full name. A
    /// <see cref="LegacyJob{TJob}"/> has the name of the legacy job it runs. For a schedule of job arguments, pass the
    /// arguments' type.
    /// </summary>
    /// <exception cref="System.ArgumentException">The type's <see cref="JobAttribute.Name"/> is not a valid name.</exception>
    public static string Of<TJob>() where TJob : class => JobNaming.GetName(typeof(TJob));
}
