using Ork.Clients;
using Ork.Clients.Providers;
using Ork.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core;

public interface ILockContext
{
	ILockContext UsePolicy(string policyId);
	Task<LockResponse> Lock();
}

