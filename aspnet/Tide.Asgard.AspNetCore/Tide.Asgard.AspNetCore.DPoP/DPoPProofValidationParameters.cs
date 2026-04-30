// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using System.Security.Claims;

namespace Tide.Asgard.AspNetCore.DPoP;

/// <summary>
///     Encapsulates all parameters required for validating a DPoP proof token in the Asgard ASP.NET Core API Authentication
///     Library.
/// </summary>
/// <remarks>
///     Used internally to pass context for DPoP proof validation, including HTTP method, URI, proof token, and related
///     access token claims.
/// </remarks>
public sealed record DPoPProofValidationParameters
{
    /// <summary>
    ///     The HTTP URI (htu) for which the DPoP proof is being validated.
    /// </summary>
    public required string Htu { get; init; }

    /// <summary>
    ///     The HTTP method (htm) used in the request (e.g., GET, POST).
    /// </summary>
    public required string Htm { get; init; }

    /// <summary>
    ///     The raw DPoP proof JWT token provided by the client.
    /// </summary>
    public required string ProofToken { get; init; }

    /// <summary>
    ///     The access token associated with the request, if available.
    /// </summary>
    public required string? AccessToken { get; init; }

    /// <summary>
    ///     Claims extracted from the access token, if available.
    /// </summary>
    public IEnumerable<Claim>? AccessTokenClaims { get; init; } = [];

    /// <summary>
    ///     DPoP-specific options used for validation.
    /// </summary>
    public required DPoPOptions Options { get; init; }
}
