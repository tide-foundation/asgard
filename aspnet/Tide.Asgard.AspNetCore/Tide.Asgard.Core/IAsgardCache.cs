using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.Core;

public interface IAsgardCache
{
	IPolicyProvider PolicyProvider { get; }

	Task<ReadOnlyMemory<byte>> GetPolicy(string id);
	/// <summary>
	/// Refresh policy from provider
	/// </summary>
	/// <param name="id"></param>
	Task<ReadOnlyMemory<byte>> UpdatePolicy(string id);
	void RemovePolicy(string id);

	Task<string?> GetApplicationToken(string id);
	Task AddApplicationToken(string id, string token, DateTime expiry);

	Task<string?> GetApplicationTideDoken(string id);
	Task AddApplicationTideDoken(string id, string doken, DateTime expiry);
}

public abstract class BaseAsgardCache : IAsgardCache
{
	public IPolicyProvider PolicyProvider { get; }
	public BaseAsgardCache(IPolicyProvider policyProvider)
	{
		PolicyProvider = policyProvider;
	}

	public abstract Task<ReadOnlyMemory<byte>> GetPolicy(string id);
	public abstract void RemovePolicy(string id);
	public abstract Task<ReadOnlyMemory<byte>> UpdatePolicy(string id);

	public abstract Task<string?> GetApplicationToken(string id);
	public abstract Task AddApplicationToken(string id, string token, DateTime expiry);

	public abstract Task<string?> GetApplicationTideDoken(string id);
	public abstract Task AddApplicationTideDoken(string id, string doken, DateTime expiry);
}