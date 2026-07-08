using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ork.Clients;
using Ork.Models;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;
using Tide.Asgard.Core;
using Tide.Asgard.Core.PolicyHelpers;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[Route("[controller]")]
	public class AccountController(IAspAsgardService asgardService) : ControllerBase
	{
		[HttpGet]
		public async Task<IActionResult> EncryptAccount() 
		{
			// set up encryption options
			var lockOptions = new LockOptions()
				.AddItemToLock(new ItemToLock
				{
					ItemId = "id1",
					Tags = ["staff data", "date of birth"],
					Data = Encoding.UTF8.GetBytes("hello!"),
				})
				.AddItemToLock(new ItemToLock
				{
					ItemId = "id2",
					Tags = ["staff data", "date of birth"],
					Data = Encoding.UTF8.GetBytes("hellsssso!"),
				});

			LockResponse response = await asgardService.CreateLockContext(lockOptions)
				.UsePolicy("vendor:user1:assessment1:policy1")
				.Lock();

			// get the cipher from the encrypted response
			var cipher = response.GetLockedItemById("id1").Cipher;

			var cipher2 = response.GetLockedItemById("id2").Cipher;

			var cipherButDifferentFetch = response.LockedItems.First().Cipher;

			// if i want to contact another asgard enabled service
			var client = asgardService.GetHttpClient();
			await client.GetAsync("");

			return Ok(cipher);
		}
	}
}
