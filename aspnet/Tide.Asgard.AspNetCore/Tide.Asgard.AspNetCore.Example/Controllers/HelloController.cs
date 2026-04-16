using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class HelloController : ControllerBase
	{
		private readonly ILogger<HelloController> _logger;
		private readonly TokenExchangeService _tokenExchangeService;

		public HelloController(ILogger<HelloController> logger, TokenExchangeService txService)
		{
			_logger = logger;
			_tokenExchangeService = txService;
		}

		[HttpGet]
		public IActionResult Get()
		{
			_logger.LogInformation("Hello Requested");
			return Ok("Hello!");
		}

		[HttpGet]
		public async Task<IActionResult> Ping()
		{
			var token = await _tokenExchangeService.ExchangeToken(HttpContext.Request.Headers);
			return Ok("Ping!");

		}
	}
}
