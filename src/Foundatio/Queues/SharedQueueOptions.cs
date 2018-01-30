using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Serializer;
using Foundatio.Utility;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public class SharedQueueOptions<T> : SharedOptions where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public IEnumerable<IQueueBehavior<T>> Behaviors { get; set; }
    }

    public class SharedQueueOptionsBuilder<T, TOptions, TBuilder> : SharedOptionsBuilder<TOptions, TBuilder> 
        where T : class
        where TOptions : SharedQueueOptions<T>, new()
        where TBuilder : SharedQueueOptionsBuilder<T, TOptions, TBuilder> {
        public TBuilder Name(string name) {
            Target.Name = name;
            return (TBuilder)this;
        }

        public TBuilder Retries(int retries) {
            Target.Retries = retries;
            return (TBuilder)this;
        }

        public TBuilder WorkItemTimeout(TimeSpan timeout) {
            Target.WorkItemTimeout = timeout;
            return (TBuilder)this;
        }

        public TBuilder Behaviors(IEnumerable<IQueueBehavior<T>> behaviors) {
            Target.Behaviors = behaviors;
            return (TBuilder)this;
        }

        public TBuilder AddBehavior(IQueueBehavior<T> behavior) {
            if (behavior == null)
                throw new ArgumentNullException(nameof(behavior));
            if (Target.Behaviors == null)
                Target.Behaviors = new[] { behavior };
            else
                Target.Behaviors = Target.Behaviors.Concat(new[] { behavior });

            return (TBuilder)this;
        }
    }
}