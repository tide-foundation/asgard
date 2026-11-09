using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

namespace Asgard.NegativeServer.Controllers;

[Authorize]
[ApiController]
public class ProbeController : ControllerBase
{
	/// <summary>Which binding Asgard checked the token against: "dpop" or "mtls".</summary>
	[HttpGet("bound")]
	public IActionResult Bound() => Ok(new { binding = HttpContext.Items["Asgard.TokenBinding"] });

	/// <summary>Gets past the delegation filter and stops there - nothing is exchanged.</summary>
	[HttpGet("delegated")]
	[RequireExchangeApproval]
	public IActionResult Delegated() => Ok(new { delegated = true });
}
