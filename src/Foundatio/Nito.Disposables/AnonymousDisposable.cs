using System;

namespace Foundatio.Disposables
{
    /// <summary>
    /// A disposable that executes a delegate when disposed.
    /// </summary>
    public sealed class AnonymousDisposable : SingleDisposable<Action>
    {
        /// <summary>
        /// Creates a new disposable that executes <paramref name="dispose"/> when disposed.
        /// </summary>
        /// <param name="dispose">The delegate to execute when disposed. If this is <c>null</c>, then this instance does nothing when it is disposed.</param>
        public AnonymousDisposable(Action dispose)
            : base(dispose)
        {
        }

        /// <inheritdoc />
        protected override void Dispose(Action context) => context?.Invoke();

        /// <summary>
        /// Adds a delegate to be executed when this instance is disposed. If this instance is already disposed or disposing, then <paramref name="dispose"/> is executed immediately.
        /// </summary>
        /// <param name="dispose">The delegate to add. May be <c>null</c> to indicate no additional action.</param>
        public void Add(Action dispose)
        {
            if (dispose == null)
                return;
            if (!TryUpdateContext(x => x + dispose))
                dispose();
        }

        /// <summary>
        /// Creates a new disposable that executes <paramref name="dispose"/> when disposed.
        /// </summary>
        /// <param name="dispose">The delegate to execute when disposed. May not be <c>null</c>.</param>
        public static AnonymousDisposable Create(Action dispose) => new AnonymousDisposable(dispose);
    }
}
