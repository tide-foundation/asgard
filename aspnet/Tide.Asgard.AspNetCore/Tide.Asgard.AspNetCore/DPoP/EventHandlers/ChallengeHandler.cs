// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Tide.Asgard.AspNetCore.DPoP;

namespace Tide.Asgard.AspNetCore.DPoP.EventHandlers;


/// <summary>
/// Handles JWT Bearer challenge events for DPoP authentication scenarios.
/// Generates appropriate WWW-Authenticate headers based on error conditions.
/// </summary>
internal class ChallengeHandler : DPoPEventHandlerBase
{
	private readonly ILogger<ChallengeHandler> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="ChallengeHandler"/> class.
	/// </summary>
	/// <param name="logger">The logger instance for logging operations.</param>
	public ChallengeHandler(ILogger<ChallengeHandler> logger)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Answers a failed DPoP binding check with a DPoP WWW-Authenticate header. Any other failure is left to the default
	/// Bearer challenge.
	/// </summary>
	/// <param name="context">The JWT Bearer challenge context containing request and response information.</param>
	public Task ChallengeFailedBinding(JwtBearerChallengeContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (context.HttpContext.Items.ContainsKey(Constants.DPoP.DPoPErrorCode))
		{
			BuildDPoPAuthenticateHeader(context);
		}

		return Task.CompletedTask;
	}

	internal void BuildDPoPAuthenticateHeader(JwtBearerChallengeContext context)
	{
		var headerConfig = new AuthenticateHeaderConfig(
			Constants.DPoP.Error.DPoPScheme,
			Constants.DPoP.DPoPErrorCode,
			Constants.DPoP.DPoPErrorDescription,
			Constants.DPoP.DPoPStatusCode,
			includeRealm: false
		);

		BuildAuthenticateHeader(context, headerConfig);
	}

	/// <summary>
	/// Builds and appends the WWW-Authenticate header for the HTTP response.
	/// </summary>
	/// <param name="context">The JWT Bearer challenge context.</param>
	/// <param name="config">Configuration specifying header format and content.</param>
	internal virtual void BuildAuthenticateHeader(JwtBearerChallengeContext context, AuthenticateHeaderConfig config)
	{
		TryGetItem(context, config.ErrorCodeKey, out var errorCode);
		TryGetItem(context, config.ErrorDescriptionKey, out var errorDescription);
		TryGetItem(context, config.StatusCodeKey, out var statusCode);

		// Ensure status code is valid HTTP status code
		HttpStatusCode httpStatusCode = statusCode as HttpStatusCode? ?? HttpStatusCode.BadRequest;
		context.Response.StatusCode = (int)httpStatusCode;

		var sb = new StringBuilder(config.Scheme);
		PopulateWwwAuthenticateHeader(sb, errorCode, errorDescription);

		if (config.IncludeRealm &&
			string.IsNullOrWhiteSpace(errorCode?.ToString()) &&
			string.IsNullOrWhiteSpace(errorDescription?.ToString()))
		{
			// adding realm only if no error or description is provided.
			sb.Append(' ').Append(Constants.DPoP.Error.DefaultRealm);
		}

		context.Response.Headers.Append(Constants.DPoP.WWWAuthenticateHeader, sb.ToString());
		context.HandleResponse();
	}

	/// <summary>
	/// Populates the WWW-Authenticate header with error code and description.
	/// Sanitizes input to prevent header injection attacks.
	/// </summary>
	/// <param name="builder">The StringBuilder to append to.</param>
	/// <param name="errorCode">The error code to include.</param>
	/// <param name="errorDescription">The error description to include.</param>
	internal void PopulateWwwAuthenticateHeader(StringBuilder builder, object? errorCode, object? errorDescription)
	{
		var code = SanitizeHeaderValue(errorCode?.ToString());
		var description = SanitizeHeaderValue(errorDescription?.ToString());

		if (!string.IsNullOrWhiteSpace(code))
		{
			builder.Append(" error=\"")
				.Append(code)
				.Append('\"');
		}

		if (!string.IsNullOrWhiteSpace(description))
		{
			if (!string.IsNullOrWhiteSpace(code))
			{
				// Append a comma only if error code was already added
				builder.Append(',');
			}

			builder.Append(" error_description=\"")
				.Append(description)
				.Append('\"');
		}
	}

	/// <summary>
	/// Sanitizes header values to prevent header injection attacks.
	/// </summary>
	/// <param name="value">The value to sanitize.</param>
	private static string? SanitizeHeaderValue(string? value)
	{
		if (string.IsNullOrEmpty(value))
			return value;

		// Remove characters that could be used for header injection
		return value.Replace('\r', ' ')
				   .Replace('\n', ' ')
				   .Replace('\t', ' ')
				   .Replace('"', '\'');
	}

	/// <summary>
	/// Retrieves an item from the HTTP context items dictionary.
	/// </summary>
	/// <param name="context">The JWT Bearer challenge context.</param>
	/// <param name="key">The key to look up in the items dictionary.</param>
	/// <param name="output">The retrieved value, or null if not found.</param>
	private static void TryGetItem(JwtBearerChallengeContext context, string key, out object? output)
	{
		context.HttpContext.Items.TryGetValue(key, out output);
	}

	/// <summary>
	/// Configuration for building WWW-Authenticate headers.
	/// Encapsulates the scheme, error keys, and formatting options.
	/// </summary>
	/// <param name="scheme">The authentication scheme (Bearer or DPoP).</param>
	/// <param name="errorCodeKey">The key for retrieving error code from context items.</param>
	/// <param name="errorDescriptionKey">The key for retrieving error description from context items.</param>
	/// <param name="statusCodeKey">The key for retrieving HTTP status code from context items.</param>
	/// <param name="includeRealm">Whether to include a realm directive when no errors are present.</param>
	internal readonly struct AuthenticateHeaderConfig(
		string scheme,
		string errorCodeKey,
		string errorDescriptionKey,
		string statusCodeKey,
		bool includeRealm)
	{
		public string Scheme { get; } = scheme;
		public string ErrorCodeKey { get; } = errorCodeKey;
		public string ErrorDescriptionKey { get; } = errorDescriptionKey;
		public string StatusCodeKey { get; } = statusCodeKey;
		public bool IncludeRealm { get; } = includeRealm;
	}
}
