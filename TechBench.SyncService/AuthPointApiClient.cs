using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TechBench.SyncService;

public sealed record AuthPointApiConfiguration(
    bool Enabled,
    string BaseApiUrl,
    string AccountId,
    string ResourceId,
    string AccessId);

public enum AuthPointMfaResultKind
{
    Approved,
    Denied,
    Error
}

public sealed record AuthPointMfaResult(
    AuthPointMfaResultKind Kind,
    string Code,
    string Message,
    string? TransactionId = null);

public sealed class AuthPointApiClient
{
    private static readonly Regex ApiHostPattern = new(
        @"^api\.[a-z0-9-]+\.cloud\.watchguard\.com$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex AccountIdPattern = new(
        @"^[A-Za-z0-9-]{3,80}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ResourceIdPattern = new(
        @"^[0-9]{1,20}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex TransactionIdPattern = new(
        @"^[A-Za-z0-9-]{16,120}$",
        RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;

    public AuthPointApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<AuthPointMfaResult> AuthenticatePushAsync(
        AuthPointApiConfiguration configuration,
        AuthPointProtectedCredentials credentials,
        string providerLogin,
        string? clientMachine,
        CancellationToken cancellationToken)
    {
        try
        {
            var baseUri = Validate(configuration, credentials, providerLogin);
            var accessToken = await RequestAccessTokenAsync(
                    baseUri,
                    configuration.AccessId,
                    credentials.AccessPassword,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var policy = await GetPolicyAsync(
                        baseUri,
                        configuration,
                        credentials.ApiKey,
                        accessToken,
                        providerLogin,
                        cancellationToken)
                    .ConfigureAwait(false);
                var policyFailure = ValidatePolicy(policy);
                if (policyFailure is not null)
                {
                    return policyFailure;
                }

                var transactionId = await StartPushAsync(
                        baseUri,
                        configuration,
                        credentials.ApiKey,
                        accessToken,
                        providerLogin,
                        clientMachine,
                        cancellationToken)
                    .ConfigureAwait(false);
                return await ValidatePushAsync(
                        baseUri,
                        configuration,
                        credentials.ApiKey,
                        accessToken,
                        transactionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                accessToken = string.Empty;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Error,
                "CANCELLED",
                "The AuthPoint request was cancelled.");
        }
        catch (TaskCanceledException)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Error,
                "PROVIDER_TIMEOUT",
                "AuthPoint did not respond before the request expired.");
        }
        catch (AuthPointApiException exception)
        {
            return new AuthPointMfaResult(
                exception.Kind,
                exception.Code,
                exception.SafeMessage,
                exception.TransactionId);
        }
        catch (Exception)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Error,
                "PROVIDER_ERROR",
                "AuthPoint could not complete the request.");
        }
    }

    internal static Uri Validate(
        AuthPointApiConfiguration configuration,
        AuthPointProtectedCredentials credentials,
        string providerLogin)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!configuration.Enabled)
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "PROVIDER_DISABLED",
                "AuthPoint protection is disabled.");
        }

        if (!Uri.TryCreate(configuration.BaseApiUrl?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && !string.IsNullOrEmpty(uri.AbsolutePath))
            || !ApiHostPattern.IsMatch(uri.IdnHost))
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "CONFIG_INVALID_HOST",
                "The configured WatchGuard API URL is not an approved HTTPS regional endpoint.");
        }

        if (!AccountIdPattern.IsMatch(configuration.AccountId ?? string.Empty)
            || !ResourceIdPattern.IsMatch(configuration.ResourceId ?? string.Empty)
            || string.IsNullOrWhiteSpace(configuration.AccessId)
            || string.IsNullOrWhiteSpace(credentials.AccessPassword)
            || string.IsNullOrWhiteSpace(credentials.ApiKey)
            || string.IsNullOrWhiteSpace(providerLogin))
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "CONFIG_INCOMPLETE",
                "The WatchGuard AuthPoint server configuration or user mapping is incomplete.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private async Task<string> RequestAccessTokenAsync(
        Uri baseUri,
        string accessId,
        string accessPassword,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(baseUri, "oauth/token"));
        var basicBytes = Encoding.UTF8.GetBytes($"{accessId}:{accessPassword}");
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(basicBytes));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(basicBytes);
        }

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string,string>("grant_type", "client_credentials"),
            new KeyValuePair<string,string>("scope", "api-access")
        ]);
        using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderStatus("TOKEN_REQUEST_FAILED", response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("access_token", out var tokenElement)
            || string.IsNullOrWhiteSpace(tokenElement.GetString()))
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "TOKEN_RESPONSE_INVALID",
                "WatchGuard returned an invalid access-token response.");
        }

        return tokenElement.GetString()!;
    }

    private async Task<AuthPointPolicyResponse> GetPolicyAsync(
        Uri baseUri,
        AuthPointApiConfiguration configuration,
        string apiKey,
        string accessToken,
        string login,
        CancellationToken cancellationToken)
    {
        var relative = BuildResourcePath(configuration, "authenticationpolicy");
        using var request = CreateApiRequest(
            HttpMethod.Post,
            new Uri(baseUri, relative),
            apiKey,
            accessToken);
        request.Content = JsonContent(new { login });
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderStatus(
                response.StatusCode == HttpStatusCode.Forbidden
                    ? "POLICY_DENIED"
                    : "POLICY_REQUEST_FAILED",
                response.StatusCode,
                deniedOnForbidden: true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<AuthPointPolicyResponse>(
                   stream,
                   JsonOptions,
                   cancellationToken)
               .ConfigureAwait(false)
            ?? throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "POLICY_RESPONSE_INVALID",
                "WatchGuard returned an invalid authentication-policy response.");
    }

    private static AuthPointMfaResult? ValidatePolicy(AuthPointPolicyResponse policy)
    {
        if (!policy.HasPolicy || !policy.IsAllowedToAuthenticate
            || policy.IsInQuarantine || policy.IsBlocked || policy.IsInOverallocated)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Denied,
                "POLICY_NOT_ALLOWED",
                "The AuthPoint policy does not allow this user to authenticate.");
        }

        if (policy.IsInForgotToken)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Denied,
                "FORGOT_TOKEN_ACTIVE",
                "AuthPoint Forgot Token is active; step-up authentication was refused.");
        }

        if (policy.PolicyResponse is null || !policy.PolicyResponse.Push)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Denied,
                "PUSH_NOT_ALLOWED",
                "The AuthPoint policy does not permit push authentication.");
        }

        if (policy.PolicyResponse.Password)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Error,
                "POLICY_PASSWORD_REQUIRED",
                "The AuthPoint REST resource must use a push-only policy because Windows authentication is TechBench's first factor.");
        }

        return null;
    }

    private async Task<string> StartPushAsync(
        Uri baseUri,
        AuthPointApiConfiguration configuration,
        string apiKey,
        string accessToken,
        string login,
        string? clientMachine,
        CancellationToken cancellationToken)
    {
        var relative = BuildResourcePath(configuration, "transactions");
        using var request = CreateApiRequest(
            HttpMethod.Post,
            new Uri(baseUri, relative),
            apiKey,
            accessToken);
        request.Content = JsonContent(new
        {
            login,
            password = string.Empty,
            type = "PUSH",
            clientInfoRequest = new
            {
                machineName = string.IsNullOrWhiteSpace(clientMachine)
                    ? "TechBench workstation"
                    : clientMachine.Trim(),
                osVersion = "Windows",
                domain = Environment.UserDomainName
            }
        });
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ProviderStatus(
                response.StatusCode == HttpStatusCode.Forbidden
                    ? "PUSH_DENIED"
                    : "PUSH_START_FAILED",
                response.StatusCode,
                deniedOnForbidden: true);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("transactionId", out var element)
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "TRANSACTION_RESPONSE_INVALID",
                "WatchGuard returned an invalid push-transaction response.");
        }

        var transactionId = element.GetString()!;
        if (!TransactionIdPattern.IsMatch(transactionId))
        {
            throw new AuthPointApiException(
                AuthPointMfaResultKind.Error,
                "TRANSACTION_ID_INVALID",
                "WatchGuard returned an invalid push-transaction identifier.");
        }

        return transactionId;
    }

    private async Task<AuthPointMfaResult> ValidatePushAsync(
        Uri baseUri,
        AuthPointApiConfiguration configuration,
        string apiKey,
        string accessToken,
        string transactionId,
        CancellationToken cancellationToken)
    {
        var relative = BuildResourcePath(
            configuration,
            "transactions/" + Uri.EscapeDataString(transactionId));
        using var request = CreateApiRequest(
            HttpMethod.Get,
            new Uri(baseUri, relative),
            apiKey,
            accessToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Denied,
                "PUSH_DENIED",
                "The AuthPoint push was denied, timed out, or could not be completed.",
                transactionId);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ProviderStatus("PUSH_VALIDATE_FAILED", response.StatusCode, transactionId: transactionId);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("authenticationResult", out var element)
            && string.Equals(element.GetString(), "AUTHORIZED", StringComparison.Ordinal))
        {
            return new AuthPointMfaResult(
                AuthPointMfaResultKind.Approved,
                "AUTHORIZED",
                "AuthPoint approved the request.",
                transactionId);
        }

        return new AuthPointMfaResult(
            AuthPointMfaResultKind.Error,
            "PUSH_RESPONSE_INVALID",
            "WatchGuard returned an unexpected push-validation response.",
            transactionId);
    }

    private static HttpRequestMessage CreateApiRequest(
        HttpMethod method,
        Uri uri,
        string apiKey,
        string accessToken)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("WatchGuard-API-Key", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static StringContent JsonContent(object value) => new(
        JsonSerializer.Serialize(value, JsonOptions),
        Encoding.UTF8,
        "application/json");

    private static string BuildResourcePath(
        AuthPointApiConfiguration configuration,
        string suffix) =>
        "rest/authpoint/authentication/v1/accounts/"
        + Uri.EscapeDataString(configuration.AccountId)
        + "/resources/"
        + Uri.EscapeDataString(configuration.ResourceId)
        + "/"
        + suffix;

    private static AuthPointApiException ProviderStatus(
        string code,
        HttpStatusCode statusCode,
        bool deniedOnForbidden = false,
        string? transactionId = null) => new(
            deniedOnForbidden && statusCode == HttpStatusCode.Forbidden
                ? AuthPointMfaResultKind.Denied
                : AuthPointMfaResultKind.Error,
            code,
            $"WatchGuard returned HTTP {(int)statusCode}; the request was not authorized.",
            transactionId);

    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record AuthPointPolicyResponse(
        bool HasPolicy,
        AuthPointPolicyMethods? PolicyResponse,
        bool IsInQuarantine,
        bool IsAllowedToAuthenticate,
        bool IsInForgotToken,
        bool IsBlocked,
        bool IsInOverallocated);

    private sealed record AuthPointPolicyMethods(bool Password, bool Push);

    private sealed class AuthPointApiException : Exception
    {
        public AuthPointApiException(
            AuthPointMfaResultKind kind,
            string code,
            string safeMessage,
            string? transactionId = null)
            : base(safeMessage)
        {
            Kind = kind;
            Code = code;
            SafeMessage = safeMessage;
            TransactionId = transactionId;
        }

        public AuthPointMfaResultKind Kind { get; }
        public string Code { get; }
        public string SafeMessage { get; }
        public string? TransactionId { get; }
    }
}
