using System;

namespace Foundatio.Logging {
    /// <summary>
    /// A class that will call an <see cref="Action"/> when Disposed.
    /// </summary>
    public class DisposeAction : IDisposable {
        private readonly Action _exitAction;

        /// <summary>
        /// Initializes a new instance of the <see cref="DisposeAction"/> class.
        /// </summary>
        /// <param name="exitAction">The exit action.</param>
        public DisposeAction(Action exitAction) {
            _exitAction = exitAction;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        void IDisposable.Dispose() {
            _exitAction.Invoke();
        }
    }
}