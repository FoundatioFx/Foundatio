using System;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio {
    public class SharedOptions {
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public class SharedOptionsBuilder<TOption, TBuilder> : OptionsBuilder<TOption>
        where TOption : SharedOptions, new()
        where TBuilder : SharedOptionsBuilder<TOption, TBuilder> {
        public TBuilder Serializer(ISerializer serializer) {
            Target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer)); ;
            return (TBuilder)this;
        }

        public TBuilder LoggerFactory(ILoggerFactory loggerFactory) {
            Target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)); ;
            return (TBuilder)this;
        }
    }
}
