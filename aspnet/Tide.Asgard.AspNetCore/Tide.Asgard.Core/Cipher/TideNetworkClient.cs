using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Cipher;

public interface ITideNetworkClient
{
	Task<LockResponse> Lock(LockOptions lockOptions);
	Task<UnlockResponse> Unlock(UnlockOptions unlockOptions);
}
public class TideNetworkClient(string authToken) : ITideNetworkClient
{
	public async Task<LockResponse> Lock(LockOptions lockOptions)
	{
		var payload = lockOptions.BuildEncryptionPayload();

		// need to build the encyption payload e.g. serialize the tide request

		// cryptide will encrypt datas and return ciphers in the order that datas was provided. use that to construct response

		ReadOnlyMemory<byte>[] ciphers = [];

		var response = new LockResponse(ciphers.Select((cipher, i) =>
		{
			var item = lockOptions.GetItemToLockAtIndex(i);
			return new LockedItem(item.ItemId, cipher);
		}));

		return response;
	}
	public async Task<UnlockResponse> Unlock(UnlockOptions unlockOptions)
	{
		throw new NotImplementedException();
	}
}
