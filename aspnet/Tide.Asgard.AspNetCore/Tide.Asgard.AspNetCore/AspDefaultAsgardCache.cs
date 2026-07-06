using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Authentication;

public class AspDefaultAsgardCache : BaseAsgardCache
{
	private readonly ConcurrentDictionary<string, (ReadOnlyMemory<byte> policy, DateTime ttl)> _policies = new();
	private readonly ConcurrentDictionary<string, (string token, DateTime ttl)> _applicationTokens = new();
	private readonly ConcurrentDictionary<string, (string token, DateTime ttl)> _applicationTideDokens = new();

	public AspDefaultAsgardCache(IHttpContextAccessor httpContextAccessor, ILogger<AspDefaultAsgardCache> logger, IPolicyProvider provider) : base(provider)
	{
		var authHeader = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
		if (!string.IsNullOrWhiteSpace(authHeader))
		{
			if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
			{
				logger.LogWarning(
					"Authorization header is present but is not a Bearer token (scheme: {Scheme}). Skipping policy provider authentication.",
					authHeader.Split(' ', 2)[0]);
				return;
			}

			var userAccessToken = authHeader["Bearer ".Length..].Trim();

			PolicyProvider.SetAuthentication(userAccessToken); // here is where we authenticate the TidecloakPolicyProvider with the user's access token, so that it can fetch policies on behalf of the user
		}
	}

	private void AddPolicy(string id, ReadOnlyMemory<byte> policy)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		var success = _policies.TryAdd(id, (policy, DateTime.UtcNow.AddMinutes(5)));
		if (!success) throw new InvalidOperationException("Could not add policy to cache possibly because it is already exists under that id");

		if (_policies.Count > 100)
		{
			// we cap the max size at 100, so if the cache is now larger than 100 policies we'll remove the policy with the oldest ttl
			var oldest = _policies.MinBy(kvp => kvp.Value.ttl);
			_policies.TryRemove(oldest.Key, out _);
		}
	}

	public override async Task<ReadOnlyMemory<byte>> GetPolicy(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		if (_policies.TryGetValue(id, out var policy))
		{
			if (policy.ttl < DateTime.UtcNow)
				return await UpdatePolicy(id);
			return policy.policy;
		}

		// try find policy from policy provider
		var fetchedPolicy = await PolicyProvider.GetPolicy(id) ?? throw new InvalidOperationException("Could not find policy in cache or provider");
		AddPolicy(id, fetchedPolicy); // add to cache
		return fetchedPolicy;
	}
	public override async Task<ReadOnlyMemory<byte>> UpdatePolicy(string id)
	{
		ReadOnlyMemory<byte>? fetchedPolicy = await PolicyProvider.GetPolicy(id);
		if (fetchedPolicy == null)
		{
			// policy doesn't exist in provider anymore... we'll remove it from cache too
			RemovePolicy(id);
			// then throw an error so we don't return null
			throw new InvalidOperationException("Could not find policy in upstream provider. Removing policy from this cache too");
		}
		_policies[id] = (fetchedPolicy.Value, DateTime.UtcNow.AddMinutes(5));
		return fetchedPolicy.Value;
	}

	public override void RemovePolicy(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		_policies.TryRemove(id, out _);
	}

	public override async Task<string?> GetApplicationToken(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		if (_applicationTokens.TryGetValue(id, out var token))
		{
			if (token.ttl < DateTime.UtcNow)
			{
				_applicationTokens.TryRemove(id, out _);
				return null;
			}
			return token.token;
		}
		return null;
	}

	public override async Task AddApplicationToken(string id, string token, DateTime expiry)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		ArgumentException.ThrowIfNullOrWhiteSpace(token, nameof(token));
		if (expiry < DateTime.UtcNow)
			throw new ArgumentException("Expiry date must be in the future", nameof(expiry));

		_applicationTokens[id] = (token, expiry);
	}

	public override async Task<string?> GetApplicationTideDoken(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		if (_applicationTideDokens.TryGetValue(id, out var doken))
		{
			if (doken.ttl < DateTime.UtcNow)
			{
				_applicationTideDokens.TryRemove(id, out _);
				return null;
			}
			return doken.token;
		}
		return null;
	}

	public override Task AddApplicationTideDoken(string id, string doken, DateTime expiry)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		ArgumentException.ThrowIfNullOrWhiteSpace(doken, nameof(doken));
		if (expiry < DateTime.UtcNow)
			throw new ArgumentException("Expiry date must be in the future", nameof(expiry));

		_applicationTideDokens[id] = (doken, expiry);
		return Task.CompletedTask;
	}
}
