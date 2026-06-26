using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core.Cipher;
using Tide.Asgard.Core.Policy;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class AccountController(ITokenExchangeService exchangeService, VendorPolicyCache policyCache, IConfiguration config) : ControllerBase
	{
		[HttpGet]
		public async Task<IActionResult> EncryptAccount()
		{
			// Get authorized tide client through token exchange of the user in this API context
			var token = await exchangeService.ExchangeToken(
				HttpContext.Request.Headers,
				config["resource"]!,
				"account-agent"
				);


			var client = new TideNetworkClient(token);


			// set up encryption options
			var lockOptions = new LockOptions
			{
				Policy = await policyCache.GetUserPolicy("userid1", "assessment1")
			};

			// add the object to encrypt
			lockOptions.AddItemToLock(new ItemToLock
			{
				ItemId = "id1",
				Tags = ["staff data", "date of birth"],
				Data = Encoding.UTF8.GetBytes("hello!"),
			});

			lockOptions.AddItemToLock(new ItemToLock
			{
				ItemId = "id2",
				Tags = ["staff data", "date of birth"],
				Data = Encoding.UTF8.GetBytes("hellsssso!"),
			});

			// encrypt using tide
			var response = await client.Lock(lockOptions);

			// get the cipher from the encrypted response
			var cipher = response.GetLockedItemById("id1").Cipher;

			var cipher2 = response.GetLockedItemById("id2").Cipher;

			var cipherButDifferentFetch = response.LockedItems.First().Cipher;

			return Ok(cipher);
		}
	}
}
