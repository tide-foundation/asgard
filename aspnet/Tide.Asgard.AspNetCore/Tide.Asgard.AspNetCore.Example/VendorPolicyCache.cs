using Tide.Asgard.Core.Policy;

namespace Tide.Asgard.AspNetCore.Example;

// THIS WILL BE IMPLEMENTED BY VENDOR (code word for current Tide client) ON THEIR SERVER
public class VendorPolicyCache(IPolicyProvider policyProvider) : DefaultPolicyCache(policyProvider)
{
	public async Task<ReadOnlyMemory<byte>> GetUserPolicy(string userid, string assessmentId)
	{
		return await GetPolicy($"vendor:{userid}:{assessmentId}");
	}
}
