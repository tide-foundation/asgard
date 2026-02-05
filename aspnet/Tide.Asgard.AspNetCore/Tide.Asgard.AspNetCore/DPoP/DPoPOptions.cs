// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using Microsoft.IdentityModel.Tokens;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Options for configuring DPoP support.
/// </summary>
public class DPoPOptions
{
    private int _iatOffset = Constants.DPoP.IatOffsetDefault;
    private int _leeway = Constants.DPoP.LeewayDefault;

    /// <summary>
    ///     The maximum allowed offset (in seconds) for the 'iat' (issued at) claim in DPoP proof tokens.
    /// </summary>
    /// <remarks>Defaults to 300 seconds.</remarks>
    public int IatOffset
    {
        get => _iatOffset;
        set => _iatOffset = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    ///     The leeway (in seconds) for time-based validation of DPoP proof tokens.
    /// </summary>
    /// <remarks>Defaults to 30 seconds</remarks>
    public int Leeway
    {
        get => _leeway;
        set => _leeway = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>
    ///     Specifies the DPoP mode. For permitted values, see <see cref="DPoPModes" />.
    /// </summary>
    /// <remarks>Defaults to <see cref="DPoPModes.Allowed" /></remarks>
    public DPoPModes Mode { get; set; } = DPoPModes.Allowed;

    /// <summary>
    ///     Token validation parameters for DPoP proof tokens.
    ///     <list type="bullet">
    ///         <item>
    ///             <description>ValidTypes: Only allows DPoP JWT type.</description>
    ///         </item>
    ///     </list>
    /// </summary>
    internal TokenValidationParameters TokenValidationParameters { get; } = new()
    {
        ValidateActor = false,
        ValidateAudience = false,
        ValidateIssuer = false,
        ValidateLifetime = false,
        ValidateSignatureLast = false,
        ValidateTokenReplay = false,
        ValidateWithLKG = false,
        ValidateIssuerSigningKey = false,
        LogValidationExceptions = false,
        ValidTypes = [Constants.DPoP.JwtTyp],
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512, ExtendedSecurityAlgorithms.EdDsa]
    };
}
