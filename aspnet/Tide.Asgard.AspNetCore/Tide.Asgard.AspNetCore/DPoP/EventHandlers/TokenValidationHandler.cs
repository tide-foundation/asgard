// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

﻿using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Tide.Asgard.AspNetCore.DPoP.EventHandlers;

internal class TokenValidationHandler : DPoPEventHandlerBase
{
	private readonly IDPoPProofValidationService _validationService;
	private readonly ILogger<TokenValidationHandler> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="TokenValidationHandler"/> class.
	/// </summary>
	/// <param name="validationService">The DPoP proof validation service.</param>
	/// <param name="logger">The logger instance for logging operations.</param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="validationService"/> or <paramref name="logger"/> is null.</exception>
	public TokenValidationHandler(IDPoPProofValidationService validationService, ILogger<TokenValidationHandler> logger)
	{
		_validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	/// Checks a token's DPoP binding. A token whose cnf claim carries no jkt thumbprint is not DPoP-bound - it is left
	/// alone for another binding's check. A DPoP-bound token must come with the DPoP scheme and a proof signed by the key
	/// it is bound to, or the context fails.
	/// </summary>
	/// <param name="context">The token validated context containing request and principal information.</param>
	/// <returns><c>true</c> only when the token is DPoP-bound and the request proved possession of its key.</returns>
	public async Task<bool> ValidateBinding(TokenValidatedContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		var cnf = context.Principal?.FindFirst(Constants.DPoP.Cnf)?.Value;
		if (string.IsNullOrEmpty(cnf)) return false; // not proof-of-possession bound at all

		try
		{
			using var document = JsonDocument.Parse(cnf);
			if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(Constants.DPoP.JwkThumbprint, out _))
				return false; // bound some other way, like mTLS's x5t#S256
		}
		catch (JsonException)
		{
			FailInvalidToken(context, Constants.DPoP.Error.Description.InvalidCnfClaim);
			return false;
		}

		if (!IsDPoPScheme(context.Request))
		{
			_logger.LogError("DPoP-bound token sent without the DPoP scheme");
			FailInvalidToken(context, Constants.DPoP.Error.Description.BearerSchemeSupportDPoPBoundTokensProof);
			return false;
		}

		if (!IsValidAuthorizationHeaderCount(context.Request) || !IsDPoPProofHeaderExists(context.Request))
		{
			_logger.LogError("DPoP-bound token sent without exactly one Authorization header and one DPoP proof header");
			HandleInvalidRequestInRequiredMode(context);
			return false;
		}

		DPoPProofValidationResult? validationResult = await ValidateAsyncInternal(context);
		if (validationResult is not { HasError: false })
		{
			CaptureErrorsInRequiredMode(context, validationResult); // the DPoP scheme's error responses
			if (context.Result is null) // an error that has no response of its own still fails
				FailInvalidToken(context, validationResult?.ErrorDescription ?? Constants.DPoP.Error.Description.UnknownError);
			return false;
		}

		context.HttpContext.Items["Asgard.TokenBinding"] = "dpop";
		return true;
	}

	/// <summary>
	/// Validates the DPoP proof for the current request using the provided <see cref="TokenValidatedContext"/>.
	/// Extracts the DPoP-bound access token and DPoP proof header, constructs validation parameters,
	/// and invokes the DPoP proof validation service.
	/// If validation fails, handles the error accordingly.
	/// </summary>
	/// <param name="context">The token validated context containing request and principal information.</param>
	/// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
	internal async Task<DPoPProofValidationResult?> ValidateAsyncInternal(TokenValidatedContext context)
	{
		DPoPProofValidationParameters validationParameters = CreateDPoPProofValidationParameters(context);

		DPoPProofValidationResult? validationResult = await _validationService.ValidateAsync(validationParameters);

		return validationResult;
	}

	/// <summary>
	/// Creates a <see cref="DPoPProofValidationParameters"/> instance from the provided <see cref="TokenValidatedContext"/>.
	/// This method extracts the DPoP proof token, access token, HTTP method, URI, claims, and DPoP options
	/// required for DPoP proof validation.
	/// </summary>
	/// <param name="context">The token validated context containing request and principal information.</param>
	/// <returns>
	/// A <see cref="DPoPProofValidationParameters"/> populated with values from the current request and context.
	/// </returns>
	internal DPoPProofValidationParameters CreateDPoPProofValidationParameters(TokenValidatedContext context)
	{
		DPoPOptions? dPoPOptions = context.HttpContext.RequestServices.GetService<DPoPOptions>();

		if (dPoPOptions is null)
		{
			throw new InvalidOperationException("Asgard DPoP options not configured.");
		}

		var validationParameters = new DPoPProofValidationParameters
		{
			ProofToken = GetDPoPProofToken(context),
			AccessToken = ExtractDPoPBoundAccessToken(context.Request),
			Htm = context.Request.Method,
			Htu = BuildHtu(context.Request),
			AccessTokenClaims = context.Principal?.Claims,
			Options = dPoPOptions
		};
		return validationParameters;
	}

	/// <summary>
	/// Retrieves the DPoP proof token from the request headers.
	/// </summary>
	/// <param name="context">The token validated context containing the HTTP request.</param>
	/// <returns>The DPoP proof token as a string, or an empty string if not present.</returns>
	internal string GetDPoPProofToken(TokenValidatedContext context)
	{
		KeyValuePair<string, StringValues> header = context.Request.Headers
			.FirstOrDefault(h =>
				string.Equals(h.Key, Constants.DPoP.ProofHeader, StringComparison.OrdinalIgnoreCase));

		return header.Key == null ? string.Empty : header.Value.ToString();
	}

	/// <summary>
	/// Captures and handles errors resulting from DPoP proof validation in Required mode.
	/// Delegates error handling to <see cref="DPoPEventHandlerBase.HandleInvalidRequestInRequiredMode"/> for InvalidRequest errors.
	/// </summary>
	/// <param name="context">The token validated context containing request and principal information.</param>
	/// <param name="validationResult">The result of DPoP proof validation.</param>
	internal void CaptureErrorsInRequiredMode(TokenValidatedContext context,
		DPoPProofValidationResult? validationResult)
	{
		CaptureErrors(context, validationResult, HandleInvalidRequestInRequiredMode);
	}

	/// <summary>
	/// Captures and handles errors resulting from DPoP proof validation.
	/// Depending on the error type, sets the error context or invokes the provided invalid request handler.
	/// </summary>
	/// <param name="context">The token validated context containing request and principal information.</param>
	/// <param name="validationResult">The result of DPoP proof validation.</param>
	/// <param name="handleInvalidRequest">
	/// Delegate to handle invalid requests, typically used for InvalidRequest errors.
	/// </param>
	internal void CaptureErrors(
		TokenValidatedContext context,
		DPoPProofValidationResult? validationResult,
		Action<TokenValidatedContext> handleInvalidRequest)
	{
		if (validationResult is { HasError: not true })
		{
			context.HttpContext.Items["Asgard.TokenBinding"] = "dpop";
			return;
		}

		switch (validationResult?.Error)
		{
			case Constants.DPoP.Error.Code.InvalidToken:
				SetErrorContext(context, validationResult, HttpStatusCode.Unauthorized);
				break;

			case Constants.DPoP.Error.Code.InvalidRequest:
				handleInvalidRequest(context);
				break;

			case Constants.DPoP.Error.Code.InvalidDPoPProof:
				SetErrorContext(context, validationResult, HttpStatusCode.BadRequest);
				break;
		}
	}

	internal static void SetErrorContext(
		TokenValidatedContext? context,
		DPoPProofValidationResult? validationResult,
		HttpStatusCode statusCode)
	{
		ArgumentNullException.ThrowIfNull(context);
		ArgumentNullException.ThrowIfNull(validationResult);

		context.HttpContext.Items[Constants.DPoP.DPoPErrorCode] = validationResult.Error;
		context.HttpContext.Items[Constants.DPoP.DPoPStatusCode] = statusCode;
		context.HttpContext.Items[Constants.DPoP.DPoPErrorDescription] = validationResult.ErrorDescription;
		context.Fail((validationResult.ErrorDescription ?? validationResult.Error) ??
					 Constants.DPoP.Error.Description.UnknownError);
	}

	/// <summary>
	/// Fails a DPoP-bound token as an invalid token, with a DPoP WWW-Authenticate challenge saying why.
	/// </summary>
	private static void FailInvalidToken(TokenValidatedContext context, string description)
	{
		var validationResult = new DPoPProofValidationResult();
		validationResult.SetError(Constants.DPoP.Error.Code.InvalidToken, description);
		SetErrorContext(context, validationResult, HttpStatusCode.Unauthorized);
	}

	/// <summary>
	/// Builds a valid HTU (HTTP URI) string from the provided <see cref="HttpRequest"/>.
	/// The resulting URI includes the scheme, host, and path, and omits the port if it is the default for the scheme.
	/// Throws <see cref="InvalidOperationException"/> if the URI components are invalid.
	/// </summary>
	/// <param name="request">The HTTP request to extract URI components from.</param>
	/// <returns>A string representing the DPoP-compliant HTU.</returns>
	/// <exception cref="InvalidOperationException">Thrown when URI components are invalid.</exception>
	internal static string BuildHtu(HttpRequest request)
	{
		try
		{
			var uriBuilder = new UriBuilder(request.Scheme, request.Host.Host);

			if (request.Host.Port.HasValue)
			{
				var port = request.Host.Port.Value;
				var isDefaultPort = request.Scheme == "https" && port == 443 ||
									request.Scheme == "http" && port == 80;

				// Only include port if it's not a default port
				uriBuilder.Port = isDefaultPort ? -1 : port;
			}
			else
			{
				uriBuilder.Port = -1;
			}

			uriBuilder.Path = request.PathBase + request.Path;
			return uriBuilder.Uri.ToString();
		}
		catch (UriFormatException ex)
		{
			throw new InvalidOperationException($"Invalid URI components: {ex.Message}", ex);
		}
	}
}
