// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

namespace Tide.Asgard.AspNetCore.DPoP;

public interface IDPoPProofValidationService
{
    /// <summary>
    ///     Validates a DPoP proof using the provided validation parameters.
    /// </summary>
    /// <param name="validationParameters">Parameters required for DPoP proof validation.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>
    ///     A <see cref="DPoPProofValidationResult" /> representing the outcome of the validation.
    /// </returns>
    Task<DPoPProofValidationResult?> ValidateAsync(DPoPProofValidationParameters validationParameters,
        CancellationToken cancellationToken = default);
}
