using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Cipher;

public class UnlockResponse(IEnumerable<UnlockedItem> lockedItems)
{
	public IEnumerable<UnlockedItem> UnlockedItems { get; } = lockedItems;
	public UnlockedItem GetLockedItemById(string itemId) => UnlockedItems.FirstOrDefault(e => e.ItemId == itemId) ?? throw new KeyNotFoundException("Could not find unlocked item by index");
}
/// <summary>
/// Data structure to store the recently unlocked data.
/// </summary>
/// <param name="itemId"></param>
/// <param name="rawBytes"></param>
public class UnlockedItem(string? itemId, ReadOnlyMemory<byte> rawBytes)
{
	public string? ItemId { get; } = itemId;
	/// <summary>
	/// The unlocked data.
	/// </summary>
	public ReadOnlyMemory<byte> Data { get; } = rawBytes;
}
