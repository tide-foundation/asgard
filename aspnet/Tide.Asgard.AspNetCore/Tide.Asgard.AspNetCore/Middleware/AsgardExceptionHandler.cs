using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.Middleware;

public class AsgardExceptionHandler(IDeviceKeyProvider deviceKeyProvider) : IExceptionHandler
{
	public async ValueTask<bool> TryHandleAsync(
			HttpContext httpContext,
			Exception exception,
			CancellationToken cancellationToken)
	{
		if (exception is not AsgardException ex)
			return false; // not ours — let the next handler / default deal with it

		httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
		httpContext.Response.Headers.Append("Tide_Exception", "Doken Requested");
		httpContext.Response.Headers.Append("Application_Key", await deviceKeyProvider.GetDeviceKeyAsString());
		return true; 
	}
}
