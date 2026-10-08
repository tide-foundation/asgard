using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[RequireExchangeApproval(ApprovalRequirement.DPoP)]
	[Route("[controller]")]
	public class PolicyController(IAspAsgardService asgardService, TidecloakPolicyProvider policyProvider) : Controller
	{
		private static readonly string PolicyId = Guid.NewGuid().ToString();

		[HttpGet("Create")]
		public async Task<IActionResult> Create()
		{
			var token = await asgardService.GetApplicationAccessToken();

			policyProvider.SetAuthentication(token);

			// check the policy hasn't already been created
			if(await policyProvider.GetPolicy(PolicyId) != null)
			{
				return Ok("Policy already exists");
			}

			var policyBuiler = new PolicyBuilder(asgardService.GetSettings().VendorId, "SimpleTagBasedDecryption:1");

			policyBuiler.BypassExplicitUserConsent();
			policyBuiler.UseForEncyption();

			var CRid = await policyProvider.AddPolicyWithChangeRequest(PolicyId, policyBuiler.BuildPolicy());

			return Ok(CRid);
		}
		public static string GetPolicyId() => PolicyId;
	}
}
