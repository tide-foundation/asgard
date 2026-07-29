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
	SessionKeyApprovalNotFound = 4

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
		_ => "Unknown error",
	};
}
