using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.AspNetCore.DPoP.Exchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[RequireDPoPExchangeApproval]
	[Route("[controller]")]
	public class PolicyController(IConfiguration config, TidecloakPolicyProvider policyProvider, ITokenExchangeService tokenExchangeService, IAsgardCache cache) : Controller
	{
		[HttpGet("Create")]
		public async Task<IActionResult> Create()
		{
			var userJti = User.FindFirst("jti")?.Value!;
			var token = await cache.GetApplicationToken(userJti);
			if (token == null)
			{
				token = await tokenExchangeService.ExchangeToken(config.GetSection("Keycloak")["resource"]!);
				await cache.AddApplicationToken(userJti, token, DateTime.UtcNow.AddMinutes(5));
			}

			policyProvider.SetAuthentication(token);

			var policyBuiler = new PolicyBuilder("vendorid", "GenericRealmAccessThresholdRole:1");

			policyBuiler.AllowPublicUse();
			policyBuiler.BypassExplicitUserConsent();
			policyBuiler.UseForEncyption();
			policyBuiler.AddParameter("role", "test-role");
			policyBuiler.AddParameter("threshold", 1);

			var CRid = await policyProvider.AddPolicyWithChangeRequest("test-policy", policyBuiler.BuildPolicy());

			return Ok(CRid);
		}
	}
}
