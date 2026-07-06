using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core;

public enum AsgardErrorCode
{
	Unknown = 0,
	DokenNotFound = 1,
	InvalidDoken = 2,

}
public class AsgardException : Exception
{
	public AsgardErrorCode Code { get; }

	public AsgardException(AsgardErrorCode code)
		: base(GetMessage(code))
	{
		Code = code;
	}

	private static string GetMessage(AsgardErrorCode code) => code switch
	{
		AsgardErrorCode.DokenNotFound => "Doken not found",
		AsgardErrorCode.InvalidDoken => "Invalid Doken",
		_ => "Unknown error",
	};
}
