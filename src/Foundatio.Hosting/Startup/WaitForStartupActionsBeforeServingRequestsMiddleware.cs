using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Hosting.Startup {
    public class WaitForStartupActionsBeforeServingRequestsMiddleware {
        private readonly StartupActionsContext _context;
        private readonly RequestDelegate _next;
#if NETSTANDARD2_0
        private readonly IApplicationLifetime _applicationLifetime;
#else
        private readonly IHostApplicationLifetime _applicationLifetime;
#endif

#if NETSTANDARD2_0
        public WaitForStartupActionsBeforeServingRequestsMiddleware(StartupActionsContext context, RequestDelegate next, IApplicationLifetime applicationLifetime) {
#else
        public WaitForStartupActionsBeforeServingRequestsMiddleware(StartupActionsContext context, RequestDelegate next, IHostApplicationLifetime applicationLifetime) {
#endif
            _context = context;
            _next = next;
            _applicationLifetime = applicationLifetime;
        }

        public async Task Invoke(HttpContext httpContext) {
            if (_context.IsStartupComplete && _context.Result.Success) {
                await _next(httpContext).AnyContext();
            } else if (_context.IsStartupComplete && !_context.Result.Success) {
                // kill the server if the startup actions failed
                _applicationLifetime.StopApplication();
            } else {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                httpContext.Response.Headers["Retry-After"] = "10";
                await httpContext.Response.WriteAsync("Service Unavailable").AnyContext();
            }
        }
    }
}