using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Ork.Clients.Providers;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.Middleware;

/// <summary>
/// You must execute app.UseExceptionHandler() in the app pipleline
/// </summary>
public class AsgardExceptionHandler : IExceptionHandler
{
	public ValueTask<bool> TryHandleAsync(
			HttpContext httpContext,
			Exception exception,
			CancellationToken cancellationToken)
	{
		if (exception is not AsgardException ex)
			return ValueTask.FromResult(false); // not ours — let the next handler / default deal with it

		httpContext.Response.StatusCode = ex.HttpErrorCode;
		httpContext.Response.Headers["Asgard-Exception"] = ex.Code.ToString();
		foreach((var headerName, var headerValue) in ex.ResponseHeaders)
		{
			httpContext.Response.Headers[headerName] = headerValue;
		}
		return ValueTask.FromResult(true); 
	}
}
