using Microsoft.AspNetCore.Http;
using Ork.Clients;
using Ork.Models;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.Locking;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

public class AspAsgardService(
	TideClientManagerProvider tideClientManagerProvider,
	IAsgardCache asgardCache,
	IHttpContextAccessor httpContextAccessor,
	ITokenExchangeService tokenExchangeService,
	IHttpClientFactory httpClientFactory
	) : IAspAsgardService
{
	public ILockContext CreateLockContext(LockOptions lockOptions)
	{
		var context = new AspLockContext(asgardCache, tideClientManagerProvider, lockOptions, httpContextAccessor, tokenExchangeService);
		return context;
	}

	public HttpClient GetHttpClient() => httpClientFactory.CreateClient("Asgard");
}
