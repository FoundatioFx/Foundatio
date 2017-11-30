using System;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Logging;

namespace Foundatio.Jobs.Commands {
    public class JobCommandContext {
        public JobCommandContext(CommandLineApplication app, Type jobType, Lazy<IServiceProvider> serviceProvider, ILoggerFactory loggerFactory = null) {
            Application = app;
            JobType = jobType;
            ServiceProvider = serviceProvider;
            LoggerFactory = loggerFactory;
        }

        public CommandLineApplication Application { get; }
        public Type JobType { get; }
        public Lazy<IServiceProvider> ServiceProvider { get; }
        public ILoggerFactory LoggerFactory { get; }
    }
}
