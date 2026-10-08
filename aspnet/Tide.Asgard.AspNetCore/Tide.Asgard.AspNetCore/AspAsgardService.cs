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
	IHttpClientFactory httpClientFactory,
	AsgardSettings asgardSettings
	) : IAspAsgardService
{
	public ILockContext CreateLockContext(LockOptions lockOptions)
	{
		var context = new AspLockContext(asgardCache, tideClientManagerProvider, lockOptions, httpContextAccessor, tokenExchangeService);
		return context;
	}

	public HttpClient GetHttpClient() => httpClientFactory.CreateClient("Asgard");
	public async Task<string> GetApplicationAccessToken()
	{
		var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException($"HTTP context is not available. Ensure {nameof(AspAsgardService)} is only used in Controllers");
		var tokenId = context.User.GetId();
		var token = await asgardCache.GetApplicationToken(tokenId);
		if (token == null)
		{
			var resp = await tokenExchangeService.ExchangeToken();
			token = resp.ApplicationAccessToken;
			await asgardCache.AddApplicationToken(tokenId, token, resp.ExpiresAt);
		}
		return token;
	}
	public AsgardSettings GetSettings() => asgardSettings;
}
