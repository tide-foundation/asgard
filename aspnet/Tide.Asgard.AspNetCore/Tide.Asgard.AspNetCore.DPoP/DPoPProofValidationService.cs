// Copyright (c) Okta, Inc. and/or its affiliates. All rights reserved.
// Licensed under the Apache License, Version 2.0. See LICENSE-APACHE-2.0 in the project root.
// Modifications Copyright (c) Tide Foundation Limited.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Tide.Asgard.Core.Crypto.Ed25519;

namespace Tide.Asgard.AspNetCore.DPoP;

/// <summary>
///     Service for validating DPoP (Demonstration of Proof-of-Possession) proof tokens
///     according to the OAuth DPoP specifications.
/// </summary>
public class DPoPProofValidationService : IDPoPProofValidationService
{
    private readonly ILogger<DPoPProofValidationService> _logger;

    public DPoPProofValidationService(ILogger<DPoPProofValidationService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    ///     Handler for processing and validating JWT tokens.
    /// </summary>
    internal JsonWebTokenHandler TokenHandler { get; set; } = new();

    /// <summary>
    ///     Validates the DPoP proof token and its binding to the access token.
    /// </summary>
    /// <param name="validationParameters">Parameters required for validation.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    /// <returns>Validation result containing error state and extracted claims.</returns>
    public async Task<DPoPProofValidationResult?> ValidateAsync(DPoPProofValidationParameters validationParameters,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(validationParameters);

        var result = new DPoPProofValidationResult();

        if (string.IsNullOrWhiteSpace(validationParameters.ProofToken))
        {
            result.SetError(Constants.DPoP.Error.Code.InvalidRequest,
                Constants.DPoP.Error.Description.DPoPProofMissing);
            return result;
        }

        if (string.IsNullOrWhiteSpace(validationParameters.AccessToken))
        {
            result.SetError(Constants.DPoP.Error.Code.InvalidRequest,
                Constants.DPoP.Error.Description.AccessTokenMissing);
            return result;
        }

        await ValidateDPoPHeaderTokenAsync(validationParameters, result);

        if (result.HasError)
        {
            return result;
        }

        ValidateCnf(validationParameters, result);

        if (result.HasError)
        {
            return result;
        }

        ValidateDPoPPayload(validationParameters, result);

        return result;
    }

    /// <summary>
    ///     Validates the DPoP proof token header, extracts and validates the JWK.
    /// </summary>
    /// <param name="validationParameters">Validation parameters.</param>
    /// <param name="validationResult">Result object to populate.</param>
    internal async Task ValidateDPoPHeaderTokenAsync(DPoPProofValidationParameters validationParameters,
        DPoPProofValidationResult validationResult)
    {
        if (!TryParseProofToken(validationParameters.ProofToken, out JsonWebToken? token))
        {
            _logger.LogWarning("DPoP proof validation failed: unable to parse proof token as a valid JWT");
            validationResult.SetError(
				Constants.DPoP.Error.Code.InvalidDPoPProof,
				Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!TryExtractJsonWebKey(token, out var jwkJson))
        {
            _logger.LogWarning("DPoP proof validation failed: unable to extract JWK from proof token header");
            validationResult.SetError(
				Constants.DPoP.Error.Code.InvalidDPoPProof,
				Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!TryCreateJsonWebKey(jwkJson, out JsonWebKey? jwk))
        {
            _logger.LogWarning("DPoP proof validation failed: unable to create JsonWebKey from extracted JWK");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (jwk is { HasPrivateKey: true })
        {
            _logger.LogWarning("DPoP proof validation failed: JWK in proof token header contains a private key");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        validationResult.JsonWebKey = jwkJson;
        validationResult.JsonWebKeyThumbprint = WebEncoders.Base64UrlEncode(ExtendedJsonWebKeyConverter.ComputeJwkThumbprint(jwk!));

        await ValidateTokenSignature(validationParameters, validationResult);
    }

    /// <summary>
    ///     Validates the 'cnf' (confirmation) claim in the access token and its binding to the DPoP proof.
    /// </summary>
    /// <param name="validationParameters">Validation parameters.</param>
    /// <param name="validationResult">Result object to populate.</param>
    internal void ValidateCnf(
        DPoPProofValidationParameters validationParameters,
        DPoPProofValidationResult validationResult)
    {
        Claim? cnfClaim =
            validationParameters.AccessTokenClaims?.FirstOrDefault(c => c.Type == Constants.DPoP.Cnf);

        if (cnfClaim?.Value is null or "")
        {
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidToken, Constants.DPoP.Error.Description.CnfClaimMissing);
            return;
        }

        if (!TryParseCnfClaim(cnfClaim.Value, out Dictionary<string, JsonElement>? cnfJson))
        {
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidToken,
                Constants.DPoP.Error.Description.InvalidCnfClaim);
            return;
        }

        if (cnfJson != null && !TryValidateThumbprintBinding(cnfJson, validationResult.JsonWebKeyThumbprint))
        {
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidToken, Constants.DPoP.Error.Description.InvalidSignature);
        }
    }

    /// <summary>
    ///     Validates the payload claims of the DPoP proof token.
    /// </summary>
    /// <param name="validationParameters">Validation parameters.</param>
    /// <param name="validationResult">Result object to populate.</param>
    internal void ValidateDPoPPayload(DPoPProofValidationParameters validationParameters,
        DPoPProofValidationResult validationResult)
    {
        if (validationResult.ProofClaims == null)
        {
            _logger.LogWarning("DPoP proof validation failed: proof claims are null (signature validation may have failed)");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!ValidateAccessTokenHash(validationParameters.AccessToken, validationResult))
        {
            _logger.LogWarning("DPoP proof validation failed: 'ath' (access token hash) claim does not match the access token");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!ValidateJtiClaim(validationResult.ProofClaims))
        {
            _logger.LogWarning("DPoP proof validation failed: 'jti' claim is missing or empty");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!ValidateHtmClaim(validationParameters.Htm, validationResult.ProofClaims))
        {
            _logger.LogWarning("DPoP proof validation failed: 'htm' claim does not match request method. Expected: {ExpectedHtm}", validationParameters.Htm);
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!ValidateHtuClaim(validationParameters.Htu, validationResult.ProofClaims))
        {
            validationResult.ProofClaims.TryGetValue(Constants.DPoP.Htu, out var htuClaim);
            _logger.LogWarning("DPoP proof validation failed: 'htu' claim does not match request URI. Expected: {ExpectedHtu}, Actual: {ActualHtu}", validationParameters.Htu, htuClaim);
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
            return;
        }

        if (!ValidateIatClaim(validationParameters, validationResult))
        {
            _logger.LogWarning("DPoP proof validation failed: 'iat' claim is outside the allowed time window (offset: {IatOffset}s, leeway: {Leeway}s)", validationParameters.Options.IatOffset, validationParameters.Options.Leeway);
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
        }
    }

    /// <summary>
    ///     Attempts to parse the DPoP proof token into a JsonWebToken.
    /// </summary>
    /// <param name="proofToken">The DPoP proof token string.</param>
    /// <param name="token">Parsed JsonWebToken output.</param>
    /// <returns>True if parsing succeeds; otherwise, false.</returns>
    internal bool TryParseProofToken(string proofToken, out JsonWebToken? token)
    {
        token = null;
        try
        {
            token = TokenHandler.ReadJsonWebToken(proofToken);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Attempts to extract the JWK from the DPoP proof token header.
    /// </summary>
    /// <param name="token">The parsed JsonWebToken.</param>
    /// <param name="jwkJson">Extracted JWK as JSON string.</param>
    /// <returns>True if extraction succeeds; otherwise, false.</returns>
    internal bool TryExtractJsonWebKey(JsonWebToken? token, out string jwkJson)
    {
        jwkJson = string.Empty;
        JsonElement jwkValues = default;

        if (token == null || !token.TryGetHeaderValue(Constants.DPoP.JsonWebKey, out jwkValues))
        {
            return false;
        }

        jwkJson = JsonSerializer.Serialize(jwkValues);
        return true;
    }

    /// <summary>
    ///     Attempts to create a JsonWebKey from its JSON representation.
    /// </summary>
    /// <param name="jwkJson">JWK as JSON string.</param>
    /// <param name="jwk">Parsed JsonWebKey output.</param>
    /// <returns>True if creation succeeds; otherwise, false.</returns>
    internal bool TryCreateJsonWebKey(string jwkJson, out JsonWebKey? jwk)
    {
        jwk = null;
        try
        {
            jwk = new JsonWebKey(jwkJson);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    ///     Validates the signature of the DPoP proof token using the extracted JWK.
    /// </summary>
    /// <param name="validationParameters">Validation parameters.</param>
    /// <param name="validationResult">Result object to populate.</param>
    internal async Task ValidateTokenSignature(DPoPProofValidationParameters validationParameters,
        DPoPProofValidationResult validationResult)
    {
        try
        {
            TokenValidationParameters? tvp = validationParameters.Options.TokenValidationParameters.Clone();
			tvp.IssuerSigningKey = new JsonWebKey(validationResult.JsonWebKey).ToSecurityKey();

            TokenValidationResult? tokenValidationResult =
                await TokenHandler.ValidateTokenAsync(validationParameters.ProofToken, tvp);

            if (tokenValidationResult?.Exception != null)
            {
                _logger.LogWarning("DPoP proof validation failed: proof token signature validation returned an error");
                validationResult.SetError(
                    Constants.DPoP.Error.Code.InvalidDPoPProof,
                    Constants.DPoP.Error.Description.DPoPProofValidationFailure);
                return;
            }

            if (tokenValidationResult != null)
            {
                validationResult.ProofClaims = tokenValidationResult.Claims;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DPoP proof validation failed: exception during proof token signature validation");
            validationResult.SetError(
                Constants.DPoP.Error.Code.InvalidDPoPProof,
                Constants.DPoP.Error.Description.DPoPProofValidationFailure);
        }
    }

    /// <summary>
    ///     Attempts to parse the 'cnf' claim value into a dictionary.
    /// </summary>
    /// <param name="cnfValue">The 'cnf' claim value as JSON string.</param>
    /// <param name="cnfJson">Parsed dictionary output.</param>
    /// <returns>True if parsing succeeds; otherwise, false.</returns>
    internal bool TryParseCnfClaim(string cnfValue, out Dictionary<string, JsonElement>? cnfJson)
    {
        cnfJson = null;
        try
        {
            cnfJson = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(cnfValue);
            return cnfJson != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Validates that the thumbprint in the 'cnf' claim matches the expected JWK thumbprint.
    /// </summary>
    /// <param name="cnfJson">Parsed 'cnf' claim dictionary.</param>
    /// <param name="expectedThumbprint">Expected JWK thumbprint.</param>
    /// <returns>True if thumbprints match; otherwise, false.</returns>
    internal bool TryValidateThumbprintBinding(Dictionary<string, JsonElement> cnfJson,
        string? expectedThumbprint)
    {
        if (!cnfJson.TryGetValue(Constants.DPoP.JwkThumbprint, out JsonElement jktJson))
        {
            return false;
        }

        var accessTokenJkt = jktJson.ToString();
        return accessTokenJkt == expectedThumbprint;
    }

    /// <summary>
    ///     Validates the 'ath' (access token hash) claim in the DPoP proof token.
    /// </summary>
    /// <param name="accessToken">The access token string.</param>
    /// <param name="validationResult">Result object to populate.</param>
    /// <returns>True if hash matches; otherwise, false.</returns>
    internal bool ValidateAccessTokenHash(string? accessToken, DPoPProofValidationResult validationResult)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        object? ath = string.Empty;
        if (validationResult.ProofClaims != null &&
            !validationResult.ProofClaims.TryGetValue(Constants.DPoP.Ath, out ath))
        {
            return false;
        }

        validationResult.AccessTokenHash = ath.ToString();

        if (string.IsNullOrEmpty(validationResult.AccessTokenHash))
        {
            return false;
        }

        var computedHash = ComputeAccessTokenHash(accessToken);
        return computedHash == validationResult.AccessTokenHash;
    }

    /// <summary>
    ///     Computes the SHA-256 hash of the access token and encodes it in Base64Url.
    /// </summary>
    /// <param name="accessToken">The access token string.</param>
    /// <returns>Base64Url-encoded SHA-256 hash.</returns>
    internal string ComputeAccessTokenHash(string accessToken)
    {
        var bytes = Encoding.UTF8.GetBytes(accessToken);
        var hash = SHA256.HashData(bytes);
        return WebEncoders.Base64UrlEncode(hash);
    }

    /// <summary>
    ///     Validates the 'jti' (JWT ID) claim in the DPoP proof token.
    /// </summary>
    /// <param name="proofClaims">Claims dictionary from the proof token.</param>
    /// <returns>True if 'jti' is present and non-empty; otherwise, false.</returns>
    internal bool ValidateJtiClaim(IDictionary<string, object> proofClaims)
    {
        if (!proofClaims.TryGetValue(Constants.DPoP.Jti, out var jti))
        {
            return false;
        }

        return jti is string jtiString && !string.IsNullOrWhiteSpace(jtiString);
    }

    /// <summary>
    ///     Validates the 'htm' (HTTP method) claim in the DPoP proof token.
    /// </summary>
    /// <param name="expectedHtm">Expected HTTP method.</param>
    /// <param name="proofClaims">Claims dictionary from the proof token.</param>
    /// <returns>True if method matches; otherwise, false.</returns>
    internal bool ValidateHtmClaim(string expectedHtm, IDictionary<string, object> proofClaims)
    {
        return proofClaims.TryGetValue(Constants.DPoP.Htm, out var htm) &&
               expectedHtm.Equals(htm as string, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Validates the 'htu' (HTTP URI) claim in the DPoP proof token.
    /// </summary>
    /// <param name="expectedHtu">Expected URI.</param>
    /// <param name="proofClaims">Claims dictionary from the proof token.</param>
    /// <returns>True if URI matches; otherwise, false.</returns>
    internal bool ValidateHtuClaim(string expectedHtu, IDictionary<string, object> proofClaims)
    {
        return proofClaims.TryGetValue(Constants.DPoP.Htu, out var htu) &&
               ValidateHtuClaimValue(expectedHtu, htu as string);
    }

    /// <summary>
    ///     Validates the 'iat' (issued at) claim in the DPoP proof token.
    /// </summary>
    /// <param name="validationParameters">Validation parameters.</param>
    /// <param name="validationResult">Result object to populate.</param>
    /// <returns>True if 'iat' is within allowed time window; otherwise, false.</returns>
    internal bool ValidateIatClaim(DPoPProofValidationParameters validationParameters,
        DPoPProofValidationResult validationResult)
    {
        if (validationResult.ProofClaims is null ||
            !validationResult.ProofClaims.TryGetValue(Constants.DPoP.Iat, out var iat))
        {
            return false;
        }

        var issuedAt = ExtractIssuedAt(iat);
        if (!issuedAt.HasValue)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return issuedAt > now - validationParameters.Options.IatOffset &&
               issuedAt < now + validationParameters.Options.Leeway;
    }

    /// <summary>
    ///     Extracts the issued at value from the 'iat' claim object.
    /// </summary>
    /// <param name="iat">The 'iat' claim value.</param>
    /// <returns>Issued at as long, or null if invalid.</returns>
    internal long? ExtractIssuedAt(object iat)
    {
        return iat switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            _ => null
        };
    }

    /// <summary>
    ///     Validates the 'htu' (HTTP URI) claim from the DPoP proof token against the expected request URI.
    /// </summary>
    /// <param name="requestUri">The expected request URI.</param>
    /// <param name="htuValue">The 'htu' claim value from the DPoP proof token.</param>
    /// <returns>True if the URIs match; otherwise, false.</returns>
    internal bool ValidateHtuClaimValue(string? requestUri, string? htuValue)
    {
        return !string.IsNullOrWhiteSpace(requestUri)
               && !string.IsNullOrWhiteSpace(htuValue)
               && (string.Equals(requestUri, htuValue, StringComparison.OrdinalIgnoreCase)
                   || CompareUrisStructurally(requestUri, htuValue));
    }

    /// <summary>
    ///     Compares two URIs structurally according to DPoP specification.
    /// </summary>
    /// <param name="requestUri">Expected request URI.</param>
    /// <param name="htuValue">HTU claim value.</param>
    /// <returns>True if URIs match structurally; otherwise, false.</returns>
    internal bool CompareUrisStructurally(string requestUri, string htuValue)
    {
        if (!Uri.TryCreate(requestUri, UriKind.Absolute, out Uri? requestedUriObj) ||
            !Uri.TryCreate(htuValue, UriKind.Absolute, out Uri? htuUriObj))
        {
            return false;
        }

        if (!IsValidScheme(requestedUriObj.Scheme) || !IsValidScheme(htuUriObj.Scheme))
        {
            return false;
        }

        // Compare the URIs according to DPoP specification
        return Uri.Compare(requestedUriObj, htuUriObj,
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
    }

    /// <summary>
    ///     Checks if the provided URI scheme is valid (HTTP or HTTPS).
    /// </summary>
    /// <param name="scheme">The URI scheme to check.</param>
    /// <returns>True if the scheme is "http" or "https" (case-insensitive); otherwise, false.</returns>
    internal bool IsValidScheme(string scheme)
    {
        return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
    }
}
