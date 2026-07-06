using Cryptide.Models;

namespace Tide.Asgard.Core.PolicyHelpers;

public class PolicyBuilder(string vendorId, string contractId)
{
	private readonly SortedDictionary<string, object> _parameters = new();
	private bool allowPublicUse = false;
	private bool bypassExplicitUserConsent = false;
	private readonly List<string> allowedModelIds = [];
	public ReadOnlyMemory<byte> BuildPolicy()
	{
		var policyParams = new PolicyParameters(_parameters);
		var policy = new Policy(
			contractId, 
			allowedModelIds.AsReadOnly(), 
			vendorId, 
			bypassExplicitUserConsent ? ApprovalType.IMPLICIT : ApprovalType.EXPLICIT, 
			allowPublicUse ? ExecutionType.PUBLIC : ExecutionType.PRIVATE, 
			policyParams);
		return policy.ToBytes();
	}
	public void AddParameter(string name, string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
		ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(value));
		_parameters[name] = value;
	}
	public void AddParameter(string name, int value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
		_parameters[name] = value;
	}
	public void AllowPublicUse()
	{
		allowPublicUse = true;
	}
	public void BypassExplicitUserConsent()
	{
		bypassExplicitUserConsent = true;
	}
	public void UseForEncyption()
	{
		allowedModelIds.Add("PolicyEnabledEncryption:1");
	}
	public void UseForDecryption()
	{
		allowedModelIds.Add("PolicyEnabledDecryption:1");
	}
	public void UseForSigning(string modelId)
	{
		allowedModelIds.Add(modelId);
	}
}
