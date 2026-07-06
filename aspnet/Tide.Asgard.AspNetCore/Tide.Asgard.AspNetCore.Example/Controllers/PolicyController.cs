using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	public class PolicyController(IConfiguration config, TidecloakPolicyProvider policyProvider) : Controller
	{
		[HttpGet("Create")]
		public async Task<IActionResult> Create()
		{
			var policyBuiler = new PolicyBuilder(config["vendorId"]!, "");

			policyBuiler.AllowPublicUse();
			policyBuiler.BypassExplicitUserConsent();
			policyBuiler.UseForEncyption();

			var CRid = await policyProvider.AddPolicyWithChangeRequest("example-policy", policyBuiler.BuildPolicy());

			return Ok(CRid);
		}
	}
}
