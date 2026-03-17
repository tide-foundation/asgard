using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[ApiController]
	public class DPoPAuthController : ControllerBase
	{
		[HttpGet("tide_dpop/iss/687474703a2f2f6c6f63616c686f73743a383038302f7265616c6d732f65373536666432312d613035352d343130392d626135642d616661353336393631616337/aud/6d79636c69656e74/tide_dpop_auth.html")]
		public IActionResult Get()
		{
			Response.Headers.Remove("X-Frame-Options");
			Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'";
			Response.Headers["Allow-CSP-From"] = "*";

			var stream = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream("Tide.Asgard.AspNetCore.Example.Resources.tide_dpop_auth.html");
			return File(stream!, "text/html");
		}
	}
}
