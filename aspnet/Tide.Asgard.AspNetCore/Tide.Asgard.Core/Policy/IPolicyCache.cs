using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Policy;

public interface IPolicyCache
{
	Task<ReadOnlyMemory<byte>> GetPolicy(string id);
	/// <summary>
	/// Refresh policy from provider
	/// </summary>
	/// <param name="id"></param>
	Task<ReadOnlyMemory<byte>> UpdatePolicy(string id);
	void RemovePolicy(string id);
}

public class DefaultPolicyCache(IPolicyProvider policyProvider) : IPolicyCache
{
	private readonly ConcurrentDictionary<string, (ReadOnlyMemory<byte> policy, DateTime ttl)> _policies = new();

	private void AddPolicy(string id, ReadOnlyMemory<byte> policy)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		var success = _policies.TryAdd(id, (policy, DateTime.UtcNow.AddMinutes(5)));
		if (!success) throw new InvalidOperationException("Could not add policy to cache possibly because it is already exists under that id");

		if(_policies.Count > 100)
		{
			// we cap the max size at 100, so if the cache is now larger than 100 policies we'll remove the policy with the oldest ttl
			var oldest = _policies.MinBy(kvp => kvp.Value.ttl);
			_policies.TryRemove(oldest.Key, out _);
		}
	}

	public async Task<ReadOnlyMemory<byte>> GetPolicy(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		if (_policies.TryGetValue(id, out var policy))
		{
			if (policy.ttl < DateTime.UtcNow) 
				return await UpdatePolicy(id);
			return policy.policy;
		}

		// try find policy from policy provider
		var fetchedPolicy = await policyProvider.GetPolicy(id) ?? throw new InvalidOperationException("Could not find policy in cache or provider");
		AddPolicy(id, fetchedPolicy); // add to cache
		return fetchedPolicy;
	}
	public async Task<ReadOnlyMemory<byte>> UpdatePolicy(string id)
	{
		ReadOnlyMemory<byte>? fetchedPolicy = await policyProvider.GetPolicy(id);
		if(fetchedPolicy == null)
		{
			// policy doesn't exist in provider anymore... we'll remove it from cache too
			RemovePolicy(id);
			// then throw an error so we don't return null
			throw new InvalidOperationException("Could not find policy in upstream provider. Removing policy from this cache too");
		}
		_policies[id] = (fetchedPolicy.Value, DateTime.UtcNow.AddMinutes(5));
		return fetchedPolicy.Value;
	}

	public void RemovePolicy(string id)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id, nameof(id));
		_policies.TryRemove(id, out _);
	}
}