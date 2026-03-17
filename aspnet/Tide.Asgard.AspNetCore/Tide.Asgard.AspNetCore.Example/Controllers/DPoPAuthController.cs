using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[ApiController]
	public class DPoPAuthController : ControllerBase
	{
		[HttpGet("tide_dpop/iss/{issHex}/aud/{audHex}/tide_dpop_auth.html")]
		public IActionResult Get(string issHex, string audHex)
		{
			var issuer = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(issHex));
			var audience = System.Text.Encoding.UTF8.GetString(Convert.FromHexString(audHex));
			if(issuer != "http://localhost:8080/realms/e756fd21-a055-4109-ba5d-afa536961ac7") throw new Exception();
			if(audience != "myclient") throw new Exception();
			Response.Headers.Remove("X-Frame-Options");
			Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'unsafe-inline'";
			Response.Headers["Allow-CSP-From"] = "*";

			var stream = Assembly.GetExecutingAssembly()
				.GetManifestResourceStream("Tide.Asgard.AspNetCore.Example.Resources.tide_dpop_auth.html");
			return File(stream!, "text/html");
		}
	}
}
