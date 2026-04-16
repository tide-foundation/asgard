// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

/// <summary>
///     Specifies the available DPoP (Demonstration of Proof-of-Possession) enforcement modes.
/// </summary>
/// <remarks>
///     Permitted values are:
///     <see cref="Allowed" />,
///     <see cref="Required" /> and
///     <see cref="Disabled" />
/// </remarks>
public enum DPoPModes
{
    /// <summary>
    ///     DPoP tokens are accepted if present, but not required. Bearer tokens are also accepted.
    /// </summary>
    Allowed,

    /// <summary>
    ///     DPoP tokens must be present for authentication. Bearer tokens will be rejected.
    /// </summary>
    Required,

    /// <summary>
    ///     DPoP validation is not performed; only standard JWT Bearer tokens are accepted.
    /// </summary>
    Disabled
}
