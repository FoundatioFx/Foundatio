using System;
using System.Threading.Tasks;
using Foundatio.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Foundatio.Extensions.Hosting.Startup
{
    public class WaitForStartupActionsBeforeServingRequestsMiddleware
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly RequestDelegate _next;
        private readonly IHostApplicationLifetime _applicationLifetime;

        public WaitForStartupActionsBeforeServingRequestsMiddleware(IServiceProvider serviceProvider, RequestDelegate next, IHostApplicationLifetime applicationLifetime)
        {
            _serviceProvider = serviceProvider;
            _next = next;
            _applicationLifetime = applicationLifetime;
        }

        public async Task Invoke(HttpContext httpContext)
        {
            var startupContext = _serviceProvider.GetService<StartupActionsContext>();

            // no startup actions registered
            if (startupContext == null)
            {
                await _next(httpContext).AnyContext();
                return;
            }

            if (startupContext.IsStartupComplete && startupContext.Result.Success)
            {
                await _next(httpContext).AnyContext();
            }
            else if (startupContext.IsStartupComplete && !startupContext.Result.Success)
            {
                // kill the server if the startup actions failed
                _applicationLifetime.StopApplication();
            }
            else
            {
                httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                httpContext.Response.Headers["Retry-After"] = "10";
                await httpContext.Response.WriteAsync("Service Unavailable").AnyContext();
            }
        }
    }
}
