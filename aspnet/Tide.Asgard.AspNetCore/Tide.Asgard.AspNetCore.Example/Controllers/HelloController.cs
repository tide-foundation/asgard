using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class HelloController(ITokenExchangeService exchangeService) : ControllerBase
	{
		[HttpGet]
		public async Task<IActionResult> Get()
		{
			//var token = await exchangeService.ExchangeToken(
			//	HttpContext.Request.Headers, 
			//	"asgard_client", 
			//	"account");

			return Ok("hey");
		}
	}
}
