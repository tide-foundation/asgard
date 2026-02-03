namespace Tide.Asgard.AspNetCore.Authentication.DPoP;

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
