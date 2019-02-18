using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Foundatio.Hosting.Startup {
    public class WaitForStartupActionsBeforeServingRequestsMiddleware {
        private readonly StartupContext _context;
        private readonly RequestDelegate _next;
        private readonly IApplicationLifetime _applicationLifetime;

        public WaitForStartupActionsBeforeServingRequestsMiddleware(StartupContext context, RequestDelegate next, IApplicationLifetime applicationLifetime) {
            _context = context;
            _next = next;
            _applicationLifetime = applicationLifetime;
        }

        public async Task Invoke(HttpContext httpContext) {
            if (_context.IsStartupComplete) {
                await _next(httpContext);
            } if (_context.StartupActionsFailed) {
                _applicationLifetime.StopApplication();
            } else {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                httpContext.Response.Headers["Retry-After"] = "10";
                await httpContext.Response.WriteAsync("Service Unavailable").ConfigureAwait(false);
            }
        }
    }
}