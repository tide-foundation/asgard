using Microsoft.AspNetCore.Http;
using Ork.Clients;
using Ork.Models;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication.Locking;

internal class AspLockContext(
	IAsgardCache asgardCache,
	TideClientManagerProvider tideClientManagerProvider,
	IHttpContextAccessor httpContextAccessor,
	ITokenExchangeService tokenExchangeService,
	AsgardSettings asgardSettings
	) : ILockContext
{
	private string? PolicyId { get; set; }
	private string? Client { get; set; }
	public ILockContext UsePolicy(string policyId)
	{
		PolicyId = policyId;
		return this;
	}
	/// <summary>
	/// In case locking requires a specific resource's scope for permissions. E.g. use billing client to access the billing resource roles.
	/// </summary>
	/// <param name="clientId"></param>
	/// <returns></returns>
	public ILockContext ForClient(string clientId)
	{
		Client = clientId;
		return this;
	}
	public async Task<LockResponse> Lock(LockOptions lockOptions)
	{
		// need to get the application's tide token to initialize the lock manager AND authenticate the cache policy provider (by providing exchanged token in cache)
		var (applicationToken, applicationDoken) = await GetApplicationTokenAndDoken();

		if(asgardCache.PolicyProvider.isAuthenticated == false)
		{
			asgardCache.PolicyProvider.SetAuthentication(applicationToken);
		}

		if (PolicyId != null) lockOptions.Policy = await asgardCache.GetPolicy(PolicyId);

		var lockClient = tideClientManagerProvider.GetLockClientManager(applicationDoken);
		return await lockClient.Lock(lockOptions);
	}
	public async Task<UnlockResponse> Unlock(UnlockOptions unlockOptions)
	{
		// need to get the application's tide token to initialize the lock manager AND authenticate the cache policy provider (by providing exchanged token in cache)
		var (applicationToken, applicationDoken) = await GetApplicationTokenAndDoken();

		if (asgardCache.PolicyProvider.isAuthenticated == false)
		{
			asgardCache.PolicyProvider.SetAuthentication(applicationToken);
		}

		if (PolicyId != null) unlockOptions.Policy = await asgardCache.GetPolicy(PolicyId);

		var lockClient = tideClientManagerProvider.GetLockClientManager(applicationDoken);
		return await lockClient.Unlock(unlockOptions);
	}
	private async Task<(string, string)> GetApplicationTokenAndDoken()
	{
		var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("HttpContext is null");

		var id = context.User.GetTokenCacheId(Client);
		var existingDoken = await asgardCache.GetApplicationTideDoken(id);
		if (existingDoken == null)
		{
			// perform exchange to get a new doken
			(string applicationAccessToken, string? applicationDoken, _) = await tokenExchangeService.ExchangeToken(asgardSettings.ClientName, Client);
			return (applicationAccessToken, applicationDoken ?? throw new Exception("Failed to exchange token for application doken. Response did not include a doken"));
		}
		else return (await asgardCache.GetApplicationToken(id) ?? throw new Exception("Failed to retrieve application token from cache"), existingDoken);
	}
}