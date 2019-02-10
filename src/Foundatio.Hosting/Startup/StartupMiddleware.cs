using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Foundatio.Hosting.Startup {
    public class StartupMiddleware {
        private readonly StartupContext _context;
        private readonly RequestDelegate _next;
        private readonly StartupOptions _options;

        public StartupMiddleware(StartupContext context, RequestDelegate next, IOptions<StartupOptions> options) {
            _context = context;
            _next = next;
            _options = options.Value;
        }

        public async Task Invoke(HttpContext httpContext) {
            if (_context.IsStartupComplete) {
                await _next(httpContext);
            } else {
                httpContext.Response.StatusCode = _options.FailureResponseCode;
                httpContext.Response.Headers["Retry-After"] = _options.RetryAfterSeconds.ToString();
                await httpContext.Response.WriteAsync("Service Unavailable");
            }
        }
    }
}