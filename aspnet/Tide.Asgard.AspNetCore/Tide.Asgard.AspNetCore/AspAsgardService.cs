using Microsoft.AspNetCore.Http;
using Ork.Clients;
using Ork.Models;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.Locking;
using Tide.Asgard.AspNetCore.Authentication.mTLS;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

internal class AspAsgardService(
	TideClientManagerProvider tideClientManagerProvider,
	IAsgardCache asgardCache,
	IHttpContextAccessor httpContextAccessor,
	ITokenExchangeService tokenExchangeService,
	IHttpClientFactory httpClientFactory,
	AsgardSettings asgardSettings
	) : IAspAsgardService
{
	public ILockContext CreateLockContext(LockOptions lockOptions) => CreateLockContext(lockOptions, null);
	public ILockContext CreateLockContext(LockOptions lockOptions, string? clientIdForContext = null)
	{
		var context = new AspLockContext(asgardCache, tideClientManagerProvider, lockOptions, httpContextAccessor, tokenExchangeService, asgardSettings);
		if (clientIdForContext != null) context.ForClient(clientIdForContext);
		return context;
	}

	public HttpClient GetHttpClient(string resourceClientId)
	{
		// checked here, so a bad id fails now rather than in the middle of a TLS handshake
		if (!Utils.IsCertifiableClientId(resourceClientId))
			throw new ArgumentException($"'{resourceClientId}' is not a client id the realm CA certifies", nameof(resourceClientId));
		return httpClientFactory.CreateClient($"{MTLSAsgardClientOptions.NAME_PREFIX}:{resourceClientId}");
	}
	public async Task<string> ExchangeContextTokenForApplicationToken(string? exchangeTargetClient = null)
	{
		var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException($"HTTP context is not available. Ensure {nameof(AspAsgardService)} is only used in Controllers");
		var tokenId = context.User.GetTokenCacheId(exchangeTargetClient);
		var token = await asgardCache.GetApplicationToken(tokenId);
		if (token == null)
		{
			var resp = await tokenExchangeService.ExchangeToken(asgardSettings.ClientName, exchangeTargetClient);
			token = resp.ApplicationAccessToken;
			await asgardCache.AddApplicationToken(tokenId, token, resp.ExpiresAt);
		}
		return token;
	}
	public AsgardSettings GetSettings() => asgardSettings;
}
