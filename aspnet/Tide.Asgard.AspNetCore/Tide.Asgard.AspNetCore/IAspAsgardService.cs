using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

public interface IAspAsgardService : IAsgardService
{
	public HttpClient GetHttpClient();
}
