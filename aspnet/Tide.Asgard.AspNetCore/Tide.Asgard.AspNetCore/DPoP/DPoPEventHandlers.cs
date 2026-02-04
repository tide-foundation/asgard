// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using System.Runtime.CompilerServices;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Tide.Asgard.AspNetCore.Authentication.DPoP.EventHandlers;

[assembly: InternalsVisibleTo("Tide.Asgard.AspNetCore.Authentication.UnitTests")]
[assembly: InternalsVisibleTo("DynamicProxyGenAssembly2")]

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;
/// <summary>
///     Provides event handlers for Demonstrating Proof-of-Possession (DPoP) authentication scenarios.
///     Handles the validation and processing of DPoP tokens and proof headers based on the configured DPoP mode.
/// </summary>
internal class DPoPEventHandlers
{
    /// <summary>
    ///     Creates a message received event handler that processes the incoming request to extract the DPoP
    ///     token based on the configuration in <see cref="DPoPOptions" />.
    /// </summary>
    /// <returns>A task-based event handler function for processing JWT Bearer message received events.</returns>
    internal Func<MessageReceivedContext, Task> HandleOnMessageReceived()
    {
        return context =>
        {
            var messageReceivedHandler = context.HttpContext.RequestServices.GetService<MessageReceivedHandler>()
                                         ?? new MessageReceivedHandler();
            return messageReceivedHandler.Handle(context);
        };
    }

    /// <summary>
    ///     Creates a token validated event handler that processes DPoP proof validation.
    /// </summary>
    /// <returns>A task-based event handler function for processing JWT Bearer token validated events.</returns>
    internal Func<TokenValidatedContext, Task> HandleOnTokenValidated()
    {
        return context =>
        {
            var tokenValidationHandler = context.HttpContext.RequestServices.GetService<TokenValidationHandler>();
            if (tokenValidationHandler == null)
            {
                // Fallback if DI is not properly configured
                IDPoPProofValidationService validationService =
                    context.HttpContext.RequestServices.GetRequiredService<IDPoPProofValidationService>();
                tokenValidationHandler = new TokenValidationHandler(validationService);
            }

            return tokenValidationHandler.Handle(context);
        };
    }

    /// <summary>
    ///     Creates a challenge event handler that processes DPoP authentication challenges.
    /// </summary>
    /// <returns>A task-based event handler function for processing JWT Bearer challenge events.</returns>
    internal Func<JwtBearerChallengeContext, Task> HandleOnChallenge()
    {
        return context =>
        {
            var challengeHandler = context.HttpContext.RequestServices.GetService<ChallengeHandler>()
                                   ?? new ChallengeHandler();
            return challengeHandler.Handle(context);
        };
    }
}
