using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Policy;

public interface IPolicyProvider
{
	Task LoadPoliciesAsync();
	/// <summary>
	/// Get all policies stored in provider, indexed by an ID.
	/// </summary>
	/// <returns></returns>
	Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> GetAllPolicies();
	Task<ReadOnlyMemory<byte>?> GetPolicy(string id);
}

// assume a TidecloakPolicyProvider will be created -> stores policies on tidecloak
