using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Foundatio.Hosting.Startup {
    public class WaitForStartupActionsBeforeServingRequestsMiddleware {
        private readonly StartupContext _context;
        private readonly RequestDelegate _next;

        public WaitForStartupActionsBeforeServingRequestsMiddleware(StartupContext context, RequestDelegate next) {
            _context = context;
            _next = next;
        }

        public async Task Invoke(HttpContext httpContext) {
            if (_context.IsStartupComplete) {
                await _next(httpContext);
            } else {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                httpContext.Response.Headers["Retry-After"] = "10";
                await httpContext.Response.WriteAsync("Service Unavailable").ConfigureAwait(false);
            }
        }
    }
}