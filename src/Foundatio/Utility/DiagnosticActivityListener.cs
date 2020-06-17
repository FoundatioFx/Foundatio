using System;
using System.Diagnostics;

namespace Foundatio.Diagnostics {
    public static class DiagnosticActivityListener {
        public static IDisposable Listen(string source, string name, Action<object> onStart = null, Action<object> onStop = null) {
            var listener = new DiagnosticActivitySubscriber(source, name, onStart, onStop);
            return DiagnosticListener.AllListeners.Subscribe(listener);
        }
    }
}