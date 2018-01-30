using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public interface ISharedQueueOptions {
        string Name { get; set; }
        int Retries { get; set; }
        TimeSpan WorkItemTimeout { get; set; }
    }

    public class SharedQueueOptions<T> : SharedOptions, ISharedQueueOptions where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public ICollection<IQueueBehavior<T>> Behaviors { get; set; } = new List<IQueueBehavior<T>>();
    }

    public interface ISharedQueueOptionsBuilder : ISharedOptionsBuilder {}

    public static class SharedQueueOptionsBuilderExtensions {
        public static TBuilder Name<TBuilder>(this TBuilder builder, string name) where TBuilder: ISharedQueueOptionsBuilder {
            builder.Target<ISharedQueueOptions>().Name = name;
            return (TBuilder)builder;
        }

        public static TBuilder Retries<TBuilder>(this TBuilder builder, int retries) where TBuilder : ISharedQueueOptionsBuilder {
            builder.Target<ISharedQueueOptions>().Retries = retries;
            return (TBuilder)builder;
        }

        public static TBuilder WorkItemTimeout<TBuilder>(this TBuilder builder, TimeSpan timeout) where TBuilder : ISharedQueueOptionsBuilder {
            builder.Target<ISharedQueueOptions>().WorkItemTimeout = timeout;
            return (TBuilder)builder;
        }
    }
}