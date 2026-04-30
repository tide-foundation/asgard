using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.AspNetCore.DPoP;

public class DPoPControllerOptions
{
	public required string Issuer { get; set; }
	public required string[] Audiences { get; set; }
}
