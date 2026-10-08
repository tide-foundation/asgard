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

public class AspLockContext(
	IAsgardCache asgardCache,
	TideClientManagerProvider tideClientManagerProvider,
	LockOptions lockOptions,
	IHttpContextAccessor httpContextAccessor,
	ITokenExchangeService tokenExchangeService
	) : ILockContext
{
	private string? PolicyId { get; set; }
	public ILockContext UsePolicy(string policyId)
	{
		PolicyId = policyId;
		return this;
	}
	public async Task<LockResponse> Lock()
	{
		// need to get the application's tide token to initialize the lock manager AND authenticate the cache policy provider (by providing exchanged token in cache)
		var applicationDoken = await GetApplicationDoken();

		if (PolicyId != null) lockOptions.Policy = await asgardCache.GetPolicy(PolicyId);

		var lockClient = tideClientManagerProvider.GetLockClientManager(applicationDoken);
		return await lockClient.Lock(lockOptions);
	}
	private async Task<string> GetApplicationDoken()
	{
		var context = httpContextAccessor.HttpContext ?? throw new InvalidOperationException("HttpContext is null");

		var id = context.User.GetId();
		var existingDoken = await asgardCache.GetApplicationTideDoken(id);
		if (existingDoken == null)
		{
			// perform exchange to get a new doken
			return (await tokenExchangeService.ExchangeToken()).ApplicationDoken ?? throw new Exception("Failed to exchange token for application doken. Response did not include a doken");
		}
		else return existingDoken;
	}
}