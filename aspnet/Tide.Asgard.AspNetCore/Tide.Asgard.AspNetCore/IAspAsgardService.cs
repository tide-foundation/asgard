using Ork.Models;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

public interface IAspAsgardService : IAsgardService
{
	public ILockContext CreateLockContext(string? clientIdForContext = null);
	/// <summary>
	/// An http client that calls another resource over mutual TLS and manages resource delegation flows for you.
	/// <param name="resourceClientId">The client id of the resource to call.</param>
	public HttpClient GetHttpClient(string resourceClientId);
	/// <summary>
	/// Returns the application access token for the current context. This token is used by the application to perform actions on behalf of the user/resource.
	/// </summary>
	/// <returns></returns>
	public Task<string> ExchangeContextTokenForApplicationToken(string? requestedClient = null);
}
