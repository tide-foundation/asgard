using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class PolicyController(IConfiguration config, TidecloakPolicyProvider policyProvider, ITokenExchangeService tokenExchangeService) : Controller
	{
		[HttpGet("Create")]
		public async Task<IActionResult> Create()
		{
			// need to exchange dpop here for a token to communicate with tidecloak

			var applicationToken = await tokenExchangeService.ExchangeToken("asgard-backend", "account");


			// this WILL fail

			policyProvider.SetAuthentication(applicationToken);

			var policyBuiler = new PolicyBuilder(config.GetSection("Keycloak")["vendorId"]!, "GenericRealmAccessThresholdRole:1");

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
