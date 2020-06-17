using System;
using System.Diagnostics;

namespace Foundatio.Diagnostics {
    public static class QueuesDiagnosticSource {
        public static readonly DiagnosticSource Logger = new DiagnosticListener("Foundatio.Queues");

        public static IDisposable ListenToProcessQueueEntry(Action<object> onStart, Action<object> onStop) {
            return DiagnosticActivityListener.Listen("Foundatio.Queues", "ProcessQueueEntry", onStart: onStart, onStop: onStop);
        }
    }
}