using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Cipher;

public class LockResponse(IEnumerable<LockedItem> lockedItems)
{
	public IEnumerable<LockedItem> LockedItems { get; } = lockedItems;
	public LockedItem GetLockedItemById(string itemId) => LockedItems.FirstOrDefault(e => e.ItemId == itemId) ?? throw new KeyNotFoundException("Could not find locked item by index");
}
/// <summary>
/// Data structure to store the recently encrypted data.
/// </summary>
/// <param name="itemId"></param>
/// <param name="rawBytes"></param>
public class LockedItem(string? itemId, ReadOnlyMemory<byte> rawBytes)
{ 
	public string? ItemId { get; } = itemId;
	/// <summary>
	/// The encrypted cipher of the data.
	/// </summary>
	public ReadOnlyMemory<byte> Cipher { get; } = rawBytes;
}
