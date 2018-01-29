using System;
using System.Collections.Generic;
using System.Linq;
using Foundatio.Serializer;
using Microsoft.Extensions.Logging;

namespace Foundatio.Queues {
    public class SharedQueueOptions<T> where T : class {
        public string Name { get; set; } = typeof(T).Name;
        public int Retries { get; set; } = 2;
        public TimeSpan WorkItemTimeout { get; set; } = TimeSpan.FromMinutes(5);
        public IEnumerable<IQueueBehavior<T>> Behaviors { get; set; }
        public ISerializer Serializer { get; set; }
        public ILoggerFactory LoggerFactory { get; set; }
    }

    public class SharedQueueOptionsBuilder<T> where T: class {
        protected readonly SharedQueueOptions<T> _target;

        public SharedQueueOptionsBuilder(SharedQueueOptions<T> target) {
            _target = target;
        }

        public TOptions Name<TOptions>(string name) where TOptions : SharedQueueOptionsBuilder<T> {
            _target.Name = name;
            return (TOptions)this;
        }

        public TOptions Retries<TOptions>(int retries) where TOptions : SharedQueueOptionsBuilder<T> {
            _target.Retries = retries;
            return (TOptions)this;
        }

        public TOptions WorkItemTimeout<TOptions>(TimeSpan timeout) where TOptions : SharedQueueOptionsBuilder<T>{
            _target.WorkItemTimeout = timeout;
            return (TOptions)this;
        }

        public TOptions Behaviors<TOptions>(IEnumerable<IQueueBehavior<T>> behaviors) where TOptions : SharedQueueOptionsBuilder<T>{
            _target.Behaviors = behaviors;
            return (TOptions)this;
        }

        public TOptions AddBehavior<TOptions>(IQueueBehavior<T> behavior) where TOptions : SharedQueueOptionsBuilder<T>{
            if (behavior == null)
                throw new ArgumentNullException(nameof(behavior));
            if (_target.Behaviors == null)
                _target.Behaviors = new[] { behavior };
            else
                _target.Behaviors = _target.Behaviors.Concat(new[] { behavior });

            return (TOptions)this;
        }

        public TOptions LoggerFactory<TOptions>(ILoggerFactory loggerFactory) where TOptions : SharedQueueOptionsBuilder<T>{
            _target.LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            return (TOptions)this;
        }

        public TOptions Serializer<TOptions>(ISerializer serializer) where TOptions : SharedQueueOptionsBuilder<T>{
            _target.Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            return (TOptions)this;
        }
    }
}