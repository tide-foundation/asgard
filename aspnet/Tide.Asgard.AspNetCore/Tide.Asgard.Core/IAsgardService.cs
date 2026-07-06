using Ork.Clients;
using Ork.Clients.Providers;
using Ork.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.Core;

public interface IAsgardService
{
	ILockContext CreateLockContext(LockOptions lockOptions);
	// then we'll create Unlock Contexts + Sign Contexts
}
