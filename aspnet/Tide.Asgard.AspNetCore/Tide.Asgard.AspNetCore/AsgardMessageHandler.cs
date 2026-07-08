using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.Text;
using Tide.Asgard.Core;

namespace Tide.Asgard.AspNetCore.Authentication;

public class AsgardMessageHandler(IHttpContextAccessor httpContextAccessor) : DelegatingHandler
{
	protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		AddAsgardRequestHeaders(request);
		var response = base.Send(request, cancellationToken);
		ThrowIfAsgardError(response);
		return response;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		AddAsgardRequestHeaders(request);
		var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
		ThrowIfAsgardError(response);
		return response;
	}

	private void AddAsgardRequestHeaders(HttpRequestMessage request)
	{
		var context = httpContextAccessor.HttpContext;
		if (context is null)
			return;

		foreach (var header in context.Request.Headers)
		{
			if (!header.Key.StartsWith("Asgard-", StringComparison.OrdinalIgnoreCase))
				continue;

			if (request.Headers.Contains(header.Key))
				continue; // don't clobber a value the caller set explicitly

			request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
		}
	}

	private static void ThrowIfAsgardError(HttpResponseMessage response)
	{
		if (response.Headers.TryGetValues("Asgard-Exception", out var values) &&
			Enum.TryParse<AsgardErrorCode>(values.FirstOrDefault(), out var code))
		{
			throw new AsgardException(code, headers =>
			{
				foreach (var header in response.Headers)
				{
					if (header.Key.Equals("Asgard-Exception", StringComparison.OrdinalIgnoreCase))
						continue;

					if (header.Key.StartsWith("Asgard-", StringComparison.OrdinalIgnoreCase))
						headers[header.Key] = string.Join(",", header.Value);
				}
			});
		}
	}
}
