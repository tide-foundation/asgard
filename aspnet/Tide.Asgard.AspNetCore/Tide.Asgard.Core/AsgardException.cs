using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core;

public enum AsgardErrorCode
{
	Unknown = 0,
	DokenNotFound = 1,
	InvalidDoken = 2,
	DPoPDelegationProofNotFound = 3,
	SessionKeyApprovalNotFound = 4,
	ResourceIdentityNotRegistered = 5,
	ResourceCertificateRevoked = 6,
	ResourceCertificateClientMismatch = 7

}
public class AsgardException : Exception
{
	public AsgardErrorCode Code { get; }
	public Dictionary<string, string> ResponseHeaders { get; } = [];
	public int HttpErrorCode { get; } = 401; // default 401 (unauthorized)

	public AsgardException(AsgardErrorCode code)
		: base(GetMessage(code))
	{
		Code = code;
	}

	/// <summary>For failures that are not the caller's fault - the default 401 would misattribute those.</summary>
	public AsgardException(AsgardErrorCode code, int httpErrorCode)
		: base(GetMessage(code))
	{
		Code = code;
		HttpErrorCode = httpErrorCode;
	}

	public AsgardException(AsgardErrorCode code, Action<Dictionary<string, string>> responseHeadersAction)
		: base(GetMessage(code))
	{
		Code = code;
		var headers = new Dictionary<string, string>();
		responseHeadersAction(headers);
		foreach (var (name, value) in headers)
		{
			var key = name.StartsWith("Asgard-", StringComparison.OrdinalIgnoreCase)
				? name
				: $"Asgard-{name}";
			ResponseHeaders[key] = value;
		}
	}

	private static string GetMessage(AsgardErrorCode code) => code switch
	{
		AsgardErrorCode.DokenNotFound => "Doken not found",
		AsgardErrorCode.InvalidDoken => "Invalid Doken",
		AsgardErrorCode.ResourceCertificateRevoked => "Tidecloak has revoked this resource's certificate, so it can no longer authenticate. Enroll the resource again to get a new one.",
		AsgardErrorCode.ResourceCertificateClientMismatch => "This resource's certificate was issued for a different client. Check that you are using the correct client certificate in relation to your adaptor config",
		AsgardErrorCode.ResourceIdentityNotRegistered => "This resource has no approved Tide resource identity yet, so it cannot authenticate to Tidecloak. Approve its certificate request in Tidecloak - enrollment is retried every minute and needs no restart.",
		_ => "Unknown error",
	};
}
