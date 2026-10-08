using System;
using System.Collections.Generic;
using System.Text;
using static Tide.Asgard.Core.AsgardSettings;

namespace Tide.Asgard.Core;

public sealed class AsgardSettings(
	string clientName,
	string realmName,
	string vendorId
	)
{
	public string VendorId { get; } = vendorId;
	public string ClientName { get; } = clientName;
	public string RealmName { get; } = realmName;
}
