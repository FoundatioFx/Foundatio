using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio {
    public class SharedOptions {
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
        public ISystemClock SystemClock { get; set; }
    }

    public class SharedOptionsBuilder<TOption, TBuilder> : OptionsBuilder<TOption>
        where TOption : SharedOptions, new()
        where TBuilder : SharedOptionsBuilder<TOption, TBuilder> {
        public TBuilder Serializer(ISerializer serializer) {
            Target.Serializer = serializer;
            return (TBuilder)this;
        }

        public TBuilder LoggerFactory(ILoggerFactory loggerFactory) {
            Target.LoggerFactory = loggerFactory;
            return (TBuilder)this;
        }

        public TBuilder SystemClock(ISystemClock systemClock) {
            Target.SystemClock = systemClock;
            return (TBuilder)this;
        }
    }
}
