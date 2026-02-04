// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Represents the result of validating a DPoP proof.
///     Contains error information, validated claims, and key details.
/// </summary>
public class DPoPProofValidationResult
{
    /// <summary>
    ///     Indicates whether an error occurred during DPoP proof validation.
    /// </summary>
    public bool HasError { get; private set; }

    /// <summary>
    ///     The error code if validation failed, otherwise null.
    /// </summary>
    public string? Error { get; private set; }

    /// <summary>
    ///     A human-readable description of the error, if any.
    /// </summary>
    public string? ErrorDescription { get; private set; }

    /// <summary>
    ///     The JSON Web Key (JWK) used in the DPoP proof, if available.
    /// </summary>
    public string? JsonWebKey { get; set; }

    /// <summary>
    ///     The thumbprint of the JSON Web Key, if available.
    /// </summary>
    public string? JsonWebKeyThumbprint { get; set; }

    /// <summary>
    ///     Claims extracted from the validated DPoP proof.
    /// </summary>
    public IDictionary<string, object>? ProofClaims { get; internal set; }

    /// <summary>
    ///     The hash of the access token, if present in the DPoP proof.
    /// </summary>
    public string? AccessTokenHash { get; set; }

    /// <summary>
    ///     Sets the error state and details for the validation result.
    /// </summary>
    /// <param name="error">The error code.</param>
    /// <param name="description">A human-readable error description.</param>
    public void SetError(string? error, string? description)
    {
        Error = error;
        ErrorDescription = description;
        HasError = true;
    }
}
