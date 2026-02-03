using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class HelloController : ControllerBase
	{
		private readonly ILogger<HelloController> _logger;

		public HelloController(ILogger<HelloController> logger)
		{
			_logger = logger;
		}

		[HttpGet]
		public IActionResult Get()
		{
			_logger.LogInformation("Hello Requested");
			return Ok("Hello!");
		}
	}
}
