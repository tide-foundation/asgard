using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

public interface IAspAsgardService : IAsgardService
{
	public HttpClient GetHttpClient();
	/// <summary>
	/// Returns the application access token for the current user. This token is used by the application to perform actions on behalf of the user.
	/// </summary>
	/// <returns></returns>
	public Task<string> GetApplicationAccessToken();
}
