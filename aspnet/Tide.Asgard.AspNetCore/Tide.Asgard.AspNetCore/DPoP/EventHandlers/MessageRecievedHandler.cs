// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

﻿using Microsoft.AspNetCore.Authentication.JwtBearer;
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


internal class MessageReceivedHandler : DPoPEventHandlerBase
{
	private readonly ILogger<MessageReceivedHandler> _logger;

	/// <summary>
	/// Initializes a new instance of the <see cref="MessageReceivedHandler"/> class.
	/// </summary>
	/// <param name="logger">The logger instance for logging operations.</param>
	public MessageReceivedHandler(ILogger<MessageReceivedHandler> logger)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	/// <summary>
	///     Reads the access token from an <c>Authorization: DPoP</c> header - JwtBearer only reads the Bearer scheme
	///     itself. Whether the token really is DPoP-bound, and its proof holds, is checked once the token is validated.
	/// </summary>
	/// <param name="context">
	///     The JWT Bearer message received context containing the HTTP request.
	/// </param>
	public Task ReadDPoPToken(MessageReceivedContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		if (IsValidAuthorizationHeaderCount(context.Request) && IsDPoPScheme(context.Request))
			context.Token = ExtractDPoPBoundAccessToken(context.Request);

		return Task.CompletedTask;
	}
}

