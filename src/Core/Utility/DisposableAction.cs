using System;

namespace Foundatio.Utility {
    /// <summary>
    /// A class that will call an <see cref="Action"/> when Disposed.
    /// </summary>
    public sealed class DisposableAction : IDisposable {
        private readonly Action _exitAction;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DisposableAction"/> class.
        /// </summary>
        /// <param name="exitAction">The exit action.</param>
        public DisposableAction(Action exitAction) {
            _exitAction = exitAction;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        void IDisposable.Dispose() {
            if (_disposed)
                return;

            _exitAction();
            _disposed = true;
        }
    }
}