using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core.Cipher;

public class LockOptions
{
	public ReadOnlyMemory<byte>? Policy {  get; set; }
	private List<ItemToLock> ItemsToLock { get; set; } = [];
	/// <summary>
	/// Returning cipher text will be in the same order as the items added to this list.
	/// </summary>
	/// <param name="items"></param>
	public LockOptions AddItemsToLock(IEnumerable<ItemToLock> items)
	{
		ItemsToLock.AddRange(items);
		return this;
	}
	/// <summary>
	/// Returning cipher text will be in the same order the item appended to this list.
	/// </summary>
	/// <param name="items"></param>
	public LockOptions AddItemToLock(ItemToLock item)
	{
		ItemsToLock.Add(item);
		return this;
	}
	public ReadOnlyMemory<byte> BuildEncryptionPayload()
	{
		if (!HasEncryptionItems()) throw new Exception("No encryption items added to options.");

		// need to build the encyption payload e.g. serialize the tide request
		throw new NotImplementedException();
	}
	public ItemToLock GetItemToLockAtIndex(int index)
	{
		if (index < 0 || index >= ItemsToLock.Count)
			throw new ArgumentOutOfRangeException("Index out of bounds to retrieve encryption item.");

		return ItemsToLock[index];
	}
	public LockOptions ClearItems()
	{
		ItemsToLock.Clear();
		return this;
	}
	public bool HasEncryptionItems() => ItemsToLock.Count > 0;
}

public class ItemToLock
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
