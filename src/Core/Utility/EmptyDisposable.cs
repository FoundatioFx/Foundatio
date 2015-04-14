using System;

namespace Foundatio.Utility {
    public class EmptyDisposable : IDisposable {
        public void Dispose() {}
    }

    public static class Disposable {
        public static IDisposable Empty = new EmptyDisposable();
    }
}
