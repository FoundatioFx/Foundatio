using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundatio.Queues {
    public class SharedQueueOptions<T> : SharedOptions where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public ICollection<IQueueBehavior<T>> Behaviors { get; set; } = new List<IQueueBehavior<T>>();
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
            if (retries < 0)
                throw new ArgumentOutOfRangeException(nameof(retries));
            
            Target.Retries = retries;
            return (TBuilder)this;
        }

        public TBuilder WorkItemTimeout(TimeSpan timeout) {
            if (timeout == null)
                throw new ArgumentNullException(nameof(timeout));
            
            if (timeout < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            
            Target.WorkItemTimeout = timeout;
            return (TBuilder)this;
        }

        public TBuilder Behaviors(params IQueueBehavior<T>[] behaviors) {
            Target.Behaviors = behaviors;
            return (TBuilder)this;
        }

        public TBuilder AddBehavior(IQueueBehavior<T> behavior) {
            if (behavior == null)
                throw new ArgumentNullException(nameof(behavior));
            
            if (Target.Behaviors == null)
                Target.Behaviors = new List<IQueueBehavior<T>> ();
            Target.Behaviors.Add(behavior);

            return (TBuilder)this;
        }
    }
}