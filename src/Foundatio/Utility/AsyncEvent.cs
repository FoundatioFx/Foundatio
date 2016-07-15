using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Extensions;

namespace Foundatio.Utility {
    public class AsyncEvent<TEventArgs> : IObservable<TEventArgs>, IDisposable where TEventArgs : EventArgs {
        private readonly List<Func<object, TEventArgs, Task>> _invocationList = new List<Func<object, TEventArgs, Task>>();
        private readonly object _lockObject = new object();
        private readonly bool _parallelInvoke;

        public AsyncEvent(bool parallelInvoke = false) {
            _parallelInvoke = parallelInvoke;
        } 

        public IDisposable AddHandler(Func<object, TEventArgs, Task> callback) {
            if (callback == null)
                throw new NullReferenceException("callback is null");

            lock (_lockObject)
                _invocationList.Add(callback);

            return new EventHandlerDisposable<TEventArgs>(this, callback);
        }

        public IDisposable AddSyncHandler(Action<object, TEventArgs> callback) {
            return AddHandler((sender, args) => {
                callback(sender, args);
                return Task.CompletedTask;
            });
        }

        public void RemoveHandler(Func<object, TEventArgs, Task> callback) {
            if (callback == null)
                throw new NullReferenceException("callback is null");

            lock (_lockObject)
                _invocationList.Remove(callback);
        }

        public async Task InvokeAsync(object sender, TEventArgs eventArgs) {
            List<Func<object, TEventArgs, Task>> tmpInvocationList;

            lock (_lockObject)
                tmpInvocationList = new List<Func<object, TEventArgs, Task>>(_invocationList);

            if (_parallelInvoke)
                await Task.WhenAll(tmpInvocationList.Select(callback => callback(sender, eventArgs))).AnyContext();
            else
                foreach (var callback in tmpInvocationList)
                    await callback(sender, eventArgs).AnyContext();
        }

        public IDisposable Subscribe(IObserver<TEventArgs> observer) {
            return AddSyncHandler((sender, args) => observer.OnNext(args));
        }

        public void Dispose() {
            lock (_lockObject)
                _invocationList.Clear();
        }

        private class EventHandlerDisposable<T> : IDisposable where T : EventArgs {
            private readonly AsyncEvent<T> _event;
            private readonly Func<object, T, Task> _callback;

            public EventHandlerDisposable(AsyncEvent<T> @event, Func<object, T, Task> callback) {
                _event = @event;
                _callback = callback;
            }

            public void Dispose() {
                _event.RemoveHandler(_callback);
            }
        }
    }
}
