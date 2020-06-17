using System.Diagnostics;

namespace Foundatio.Queues {
    public static class QueueDiagnosticSource {
        public static readonly DiagnosticSource QueueLogger = new DiagnosticListener("Foundatio.Queues");
    }
}