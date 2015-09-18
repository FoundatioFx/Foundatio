using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Foundatio.Utility {
    public class AsyncEvent<TEventArgs> where TEventArgs : EventArgs {
        private readonly List<Func<object, TEventArgs, Task>> _invocationList = new List<Func<object, TEventArgs, Task>>();
        private readonly object _lockObject = new object();
        private readonly bool _parallelInvoke;

        public AsyncEvent(bool parallelInvoke = false) {
            _parallelInvoke = parallelInvoke;
        } 

        public static AsyncEvent<TEventArgs> operator +(AsyncEvent<TEventArgs> e, Func<object, TEventArgs, Task> callback) {
            if (callback == null)
                throw new NullReferenceException("callback is null");

            if (e == null)
                e = new AsyncEvent<TEventArgs>();

            lock (e._lockObject)
                e._invocationList.Add(callback);

            return e;
        }

        public static AsyncEvent<TEventArgs> operator -(AsyncEvent<TEventArgs> e, Func<object, TEventArgs, Task> callback) {
            if (callback == null)
                throw new NullReferenceException("callback is null");

            if (e == null)
                return null;

            lock (e._lockObject)
                e._invocationList.Remove(callback);

            return e;
        }

        public async Task InvokeAsync(object sender, TEventArgs eventArgs) {
            List<Func<object, TEventArgs, Task>> tmpInvocationList;

            lock (_lockObject)
                tmpInvocationList = new List<Func<object, TEventArgs, Task>>(_invocationList);

            if (_parallelInvoke)
                await Task.WhenAll(tmpInvocationList.Select(callback => callback(sender, eventArgs)));
            else
                foreach (var callback in tmpInvocationList)
                    await callback(sender, eventArgs);
        }
    }
}
