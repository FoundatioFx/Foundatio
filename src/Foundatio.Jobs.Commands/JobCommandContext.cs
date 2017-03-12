using System;
using Foundatio.Logging;
using Microsoft.Extensions.CommandLineUtils;

namespace Foundatio.Jobs.Commands {
    public class JobCommandContext {
        public JobCommandContext(CommandLineApplication app, Lazy<IServiceProvider> serviceProvider, ILoggerFactory loggerFactory = null) {
            Application = app;
            ServiceProvider = serviceProvider;
            LoggerFactory = loggerFactory;
        }

        public CommandLineApplication Application { get; }
        public Lazy<IServiceProvider> ServiceProvider { get; }
        public ILoggerFactory LoggerFactory { get; }
    }
}
