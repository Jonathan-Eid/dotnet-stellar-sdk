using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;

namespace StellarDotnetSdk.Requests;

/// <summary>
///     A preconfigured <see cref="HttpClient" /> for communicating with Stellar Horizon and Soroban RPC servers.
///     Sets default request headers (client name, version, and optional bearer token) and optionally
///     wraps the handler pipeline with <see cref="RetryingHttpMessageHandler" /> when <see cref="HttpResilienceOptions" />
///     has any feature enabled (retries, circuit breaker, or timeout — see
///     <see cref="HttpResilienceOptions.HasAnyResilienceFeatureEnabled" />; status codes alone enable nothing).
/// </summary>
public class DefaultStellarSdkHttpClient : HttpClient
{
    /// <summary>
    ///     Creates an HTTP client with some default request headers and the given bearer token.
    ///     <para>
    ///         This type is a convenience helper around <see cref="HttpClient" /> for non-DI scenarios. It is
    ///         intended to be created once and reused for the lifetime of your application, just like a regular
    ///         <see cref="HttpClient" /> instance.
    ///     </para>
    ///     <para>
    ///         In applications using dependency injection and <c>IHttpClientFactory</c>, prefer configuring a
    ///         regular <see cref="HttpClient" /> instance (potentially using <see cref="RetryingHttpMessageHandler" />
    ///         and <see cref="HttpResilienceOptions" />) instead of relying on this type directly.
    ///     </para>
    ///     <para>
    ///         By default, no automatic retries are enabled. To enable retries, pass a custom
    ///         <see cref="HttpResilienceOptions" /> instance with <c>MaxRetryCount</c> set to a positive value
    ///         (and optionally populate <c>RetryHttpStatusCodes</c> for HTTP status-code retries). For
    ///         Horizon clients use <c>HttpResilienceOptionsPresets.ForHorizon()</c>; for Stellar RPC (Soroban)
    ///         use <c>HttpResilienceOptionsPresets.ForSoroban()</c>.
    ///     </para>
    /// </summary>
    /// <param name="bearerToken">Bearer token in case the server requires it.</param>
    /// <param name="clientName">Name of the client.</param>
    /// <param name="clientVersion">Version of the client.</param>
    /// <param name="resilienceOptions">
    ///     Resilience options. If null, no resilience handler is added — requests are sent with no retries,
    ///     circuit breaker, or timeout.
    /// </param>
    /// <param name="innerHandler">
    ///     Optional inner HTTP message handler. If null, defaults to <c>SocketsHttpHandler</c> on .NET 8 and later,
    ///     and to <see cref="HttpClientHandler" /> on netstandard2.1, with gzip/deflate response decompression enabled.
    ///     Use this to inject a custom handler for testing, proxies, or custom certificate handling; a custom handler
    ///     is used as is, so set its <c>AutomaticDecompression</c> if you want compressed responses.
    /// </param>
    public DefaultStellarSdkHttpClient(
        string? bearerToken = null,
        string? clientName = null,
        string? clientVersion = null,
        HttpResilienceOptions? resilienceOptions = null,
        HttpMessageHandler? innerHandler = null)
        : base(CreateHandlerPipeline(resilienceOptions, innerHandler))
    {
        InitializeHeaders(bearerToken, clientName, clientVersion);
    }

    private void InitializeHeaders(string? bearerToken, string? clientName, string? clientVersion)
    {
        var assembly = Assembly.GetAssembly(GetType())!.GetName();
        DefaultRequestHeaders.Add("X-Client-Name", clientName ?? "stellar-dotnet-sdk");
        DefaultRequestHeaders.Add("X-Client-Version", clientVersion ?? assembly.Version!.ToString());
        if (!string.IsNullOrEmpty(bearerToken))
        {
            DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    /// <summary>
    ///     Creates the SDK's default transport handler: <c>SocketsHttpHandler</c> on .NET 8 and later, and
    ///     <see cref="HttpClientHandler" /> on netstandard2.1. It sends <c>Accept-Encoding: gzip, deflate</c> and
    ///     transparently decompresses responses, which Horizon and Stellar RPC serve compressed when asked.
    /// </summary>
    /// <param name="allowAutoRedirect">Whether the handler follows HTTP redirects.</param>
    internal static HttpMessageHandler CreateDefaultHandler(bool allowAutoRedirect = true)
    {
#if NET8_0_OR_GREATER
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
#else
        // SocketsHttpHandler is not available on netstandard2.1 reference assemblies.
        return new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
#endif
    }

    private static HttpMessageHandler CreateHandlerPipeline(
        HttpResilienceOptions? resilienceOptions,
        HttpMessageHandler? innerHandler)
    {
        var handler = innerHandler ?? CreateDefaultHandler();

        // Add resilience handler if any resilience feature is enabled (retries, circuit breaker, or
        // timeout). Status codes alone enable nothing — see HasAnyResilienceFeatureEnabled's remarks.
        if (resilienceOptions?.HasAnyResilienceFeatureEnabled == true)
        {
            return new RetryingHttpMessageHandler(handler, resilienceOptions);
        }

        return handler;
    }
}