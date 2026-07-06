using Microsoft.AspNetCore.Builder;
using System;
using System.Collections.Generic;
using System.Text;

namespace Tide.Asgard.AspNetCore.Authentication;

public static class ApplicationBuilderExtensions
{
	public static IApplicationBuilder UseAsgardInterrupt(this IApplicationBuilder app)
	{
		ArgumentNullException.ThrowIfNull(app);
		


		return app;
	}
}
