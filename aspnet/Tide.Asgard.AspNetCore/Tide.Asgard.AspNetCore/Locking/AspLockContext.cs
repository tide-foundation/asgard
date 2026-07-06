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
		if(PolicyId != null) lockOptions.Policy = await asgardCache.GetPolicy(PolicyId);

		// need to get the application's tide token to initialize the lock manager
		var userDoken = GetUserTokenFromHttpContext();
		var userDokenHashId = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(userDoken)));

		string? applicationDoken = await asgardCache.GetApplicationTideDoken(userDokenHashId);

		if (applicationDoken == null)
		{
			// exchange the current user's token for a application tide doken
			applicationDoken = await tokenExchangeService.ExchangeTideDokenForApplicationDoken();
			var expiry = DateTime.UtcNow.AddMinutes(5); // change later to doken expiry TODO ;; -----------------------------------------------------------------------

			await asgardCache.AddApplicationTideDoken(userDokenHashId, applicationDoken, expiry);
		}

		var lockClient = tideClientManagerProvider.GetLockClientManager(applicationDoken);
		return await lockClient.Lock(lockOptions);
	}
	private string GetUserTokenFromHttpContext()
	{
		var context = httpContextAccessor.HttpContext;
		if (context == null)
		{
			throw new InvalidOperationException("HttpContext is null");
		}
		var token = context.Request.Headers.Authorization.ToString();
		if (string.IsNullOrEmpty(token))
		{
			throw new InvalidOperationException("Authorization header is missing");
		}
		return token.Replace("Doken ", "");
	}
}