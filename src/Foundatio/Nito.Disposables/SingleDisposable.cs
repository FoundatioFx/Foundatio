using System;
using System.Threading;

namespace Foundatio.Disposables
{
    /// <summary>
    /// A base class for disposables that need exactly-once semantics in a threadsafe way.
    /// </summary>
    /// <typeparam name="T">The type of "context" for the derived disposable.</typeparam>
    public abstract class SingleDisposable<T> : IDisposable
        where T : class
    {
        /// <summary>
        /// The context. This may be <c>null</c>.
        /// </summary>
        private T _context;

        /// <summary>
        /// Creates a disposable for the specified context.
        /// </summary>
        /// <param name="context">The context passed to <see cref="Dispose(T)"/>. If this is <c>null</c>, then <see cref="Dispose(T)"/> will never be called.</param>
        protected SingleDisposable(T context)
        {
            _context = context;
        }

        /// <summary>
        /// The actul disposal method, called only once from <see cref="Dispose()"/>. If the context passed to the constructor of this instance is <c>null</c>, then this method is never called.
        /// </summary>
        /// <param name="context">The context for the disposal operation. This is never <c>null</c>.</param>
        protected abstract void Dispose(T context);

        /// <summary>
        /// Disposes this instance.
        /// </summary>
        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _context, null);
            if (context != null)
                Dispose(context);
        }
    }
}
