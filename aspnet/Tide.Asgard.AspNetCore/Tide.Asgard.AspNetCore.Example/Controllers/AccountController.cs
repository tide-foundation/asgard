using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ork.Clients;
using Ork.Models;
using System.Text;
using Tide.Asgard.AspNetCore.Authentication;
using Tide.Asgard.AspNetCore.Authentication.TokenExchange;

namespace Tide.Asgard.AspNetCore.Example.Controllers
{
	[Authorize]
	[ApiController]
	[RequireExchangeApproval(ApprovalRequirement.TideSecuredDPoP)]
	[Route("[controller]")]
	public class AccountController(IAspAsgardService asgardService) : ControllerBase
	{
		private static (ReadOnlyMemory<byte> dob, ReadOnlyMemory<byte> name)? EncryptedData;
		[HttpGet("Encrypt")]
		public async Task<IActionResult> EncryptAccount() 
		{
			if(EncryptedData is not null)
			{
				return Ok(EncryptedData.Value.dob);
			}

			// set up encryption options
			var lockOptions = new LockOptions()
				.AddItemToLock(new ItemToLock
				{
					ItemId = "id1",
					Tags = ["staff", "date of birth"],
					Data = Encoding.UTF8.GetBytes("10/05/1990"),
				})
				.AddItemToLock(new ItemToLock
				{
					ItemId = "id2",
					Tags = ["staff", "name"],
					Data = Encoding.UTF8.GetBytes("Janet"),
				});

			LockResponse response = await asgardService.CreateLockContext(lockOptions)
				.UsePolicy(PolicyController.GetPolicyId())
				.Lock();

			// get the cipher from the encrypted response
			var cipher = response.GetLockedItemById("id1").Cipher;

			var cipher2 = response.GetLockedItemById("id2").Cipher;

			EncryptedData = (cipher, cipher2);

			//var cipherButDifferentFetch = response.LockedItems.First().Cipher;

			// if i want to contact another asgard enabled service
			//var client = asgardService.GetHttpClient();
			//await client.GetAsync("");

			return Ok(cipher);
		}
	}
}
