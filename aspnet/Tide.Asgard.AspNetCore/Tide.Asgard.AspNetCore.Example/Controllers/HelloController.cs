using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class HelloController() : ControllerBase
	{
		[HttpGet]
		public async Task<IActionResult> Get()
		{

			return Ok("hi!");
		}
	}
}