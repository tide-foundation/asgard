using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Cipher;

public class UnlockOptions
{
	public ReadOnlyMemory<byte>? Policy { get; set; }
	private List<ItemToUnLock> ItemsToUnlock { get; set; } = [];
	/// <summary>
	/// Returning text will be in the same order as the ciphers added to this list.
	/// </summary>
	/// <param name="items"></param>
	public UnlockOptions AddItemsToLock(IEnumerable<ItemToUnLock> items)
	{
		ItemsToUnlock.AddRange(items);
		return this;
	}
	/// <summary>
	/// Returning cipher text will be in the same order the item appended to this list.
	/// </summary>
	/// <param name="items"></param>
	public UnlockOptions AddItemToLock(ItemToUnLock item)
	{
		ItemsToUnlock.Add(item);
		return this;
	}
	public ItemToUnLock GetItemToLockAtIndex(int index)
	{
		if (index < 0 || index >= ItemsToUnlock.Count)
			throw new ArgumentOutOfRangeException("Index out of bounds to retrieve encryption item.");

		return ItemsToUnlock[index];
	}
	public UnlockOptions ClearItems()
	{
		ItemsToUnlock.Clear();
		return this;
	}
	public bool HasEncryptionItems() => ItemsToUnlock.Count > 0;
}

public class ItemToUnLock
{
	/// <summary>
	/// An optional identifier to help associate which cipher text is which upon return.
	/// </summary>
	public string? ItemId { get; set; }
	/// <summary>
	/// The tags used to describe the data to be encrypted.
	/// </summary>
	public IReadOnlyList<string> Tags { get; set; } = [];
	/// <summary>
	/// The data to be locked.
	/// </summary>
	public ReadOnlyMemory<byte> Data { get; set; }
}
