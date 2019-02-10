using System;

namespace Foundatio.Hosting.Startup {
    public class StartupOptions {
        public int RetryAfterSeconds { get; set; } = 30;
        public int FailureResponseCode { get; set; } = 503;
    }
}