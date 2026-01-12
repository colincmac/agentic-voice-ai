using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Shared.Diagnostics;

namespace Showcase.ServiceDefaults;

public interface IUriOptions
{
    IUriOptions UseGet();
    IUriOptions UsePost();
    IUriOptions UseHttpMethod(HttpMethod methodToUse);
    IUriOptions UseTimeout(TimeSpan timeout);
    IUriOptions ExpectHttpCode(int codeToExpect);
    IUriOptions ExpectHttpCodes(int minCodeToExpect, int maxCodeToExpect);
    IUriOptions AddCustomHeader(string name, string value);
    IUriOptions ExpectContent(string expectedContent);
}

public class UriOptions : IUriOptions
{
    public HttpMethod? HttpMethod { get; private set; }

    public TimeSpan Timeout { get; private set; }

    public (int Min, int Max)? ExpectedHttpCodes { get; private set; }

    public string? ExpectedContent { get; private set; }

    public Uri Uri { get; }

    private readonly List<(string Name, string Value)> _headers = new();

    internal IEnumerable<(string Name, string Value)> Headers => _headers;

    public UriOptions(Uri uri)
    {
        Uri = uri;
    }

    public IUriOptions AddCustomHeader(string name, string value)
    {
        _headers.Add((name, value));
        return this;
    }

    IUriOptions IUriOptions.UseGet()
    {
        HttpMethod = HttpMethod.Get;
        return this;
    }

    IUriOptions IUriOptions.UsePost()
    {
        HttpMethod = HttpMethod.Post;
        return this;
    }

    IUriOptions IUriOptions.ExpectHttpCode(int codeToExpect)
    {
        ExpectedHttpCodes = (codeToExpect, codeToExpect);
        return this;
    }

    IUriOptions IUriOptions.ExpectHttpCodes(int minCodeToExpect, int maxCodeToExpect)
    {
        ExpectedHttpCodes = (minCodeToExpect, maxCodeToExpect);
        return this;
    }

    IUriOptions IUriOptions.UseHttpMethod(HttpMethod methodToUse)
    {
        HttpMethod = methodToUse;
        return this;
    }

    IUriOptions IUriOptions.UseTimeout(TimeSpan timeout)
    {
        Timeout = timeout;
        return this;
    }

    IUriOptions IUriOptions.ExpectContent(string expectedContent)
    {
        ExpectedContent = expectedContent;
        return this;
    }
}

public class UriHealthCheckOptions
{
    private readonly List<UriOptions> _urisOptions = new();
    internal IEnumerable<UriOptions> UrisOptions => _urisOptions;
    internal HttpMethod HttpMethod { get; private set; }
    internal TimeSpan Timeout { get; private set; }
    internal (int Min, int Max) ExpectedHttpCodes { get; private set; }

    public UriHealthCheckOptions()
    {
        ExpectedHttpCodes = (200, 299);              // DEFAULT  = HTTP Successful status codes
        HttpMethod = HttpMethod.Get;
        Timeout = TimeSpan.FromSeconds(10);
    }

    public UriHealthCheckOptions UseGet()
    {
        HttpMethod = HttpMethod.Get;
        return this;
    }

    public UriHealthCheckOptions UsePost()
    {
        HttpMethod = HttpMethod.Post;
        return this;
    }

    public UriHealthCheckOptions UseHttpMethod(HttpMethod methodToUse)
    {
        HttpMethod = methodToUse;
        return this;
    }

    public UriHealthCheckOptions UseTimeout(TimeSpan timeout)
    {
        Timeout = timeout;
        return this;
    }

    public UriHealthCheckOptions AddUri(Uri uriToAdd, Action<IUriOptions>? setup = null)
    {
        var uri = new UriOptions(uriToAdd);
        setup?.Invoke(uri);

        _urisOptions.Add(uri);

        return this;
    }

    public UriHealthCheckOptions ExpectHttpCode(int codeToExpect)
    {
        ExpectedHttpCodes = (codeToExpect, codeToExpect);
        return this;
    }

    public UriHealthCheckOptions ExpectHttpCodes(int minCodeToExpect, int maxCodeToExpect)
    {
        ExpectedHttpCodes = (minCodeToExpect, maxCodeToExpect);
        return this;
    }

    internal static UriHealthCheckOptions CreateFromUris(IEnumerable<Uri> uris)
    {
        var options = new UriHealthCheckOptions();

        foreach (var item in uris)
        {
            options.AddUri(item);
        }

        return options;
    }
}
public class UriHealthCheck : IHealthCheck
{
    private readonly UriHealthCheckOptions _options;
    private readonly Func<HttpClient> _httpClientFactory;

    public UriHealthCheck(UriHealthCheckOptions options, Func<HttpClient> httpClientFactory)
    {
        _options = Throw.IfNull(options);
        _httpClientFactory = Throw.IfNull(httpClientFactory);
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var defaultHttpMethod = _options.HttpMethod;
        var defaultExpectedStatusCodes = _options.ExpectedHttpCodes;
        var defaultTimeout = _options.Timeout;
        int idx = 0;

        try
        {
            foreach (var item in _options.UrisOptions)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new HealthCheckResult(context.Registration.FailureStatus, description: $"{nameof(UriHealthCheck)} execution is cancelled.");
                }

                var method = item.HttpMethod ?? defaultHttpMethod;
                var (Min, Max) = item.ExpectedHttpCodes ?? defaultExpectedStatusCodes;
                var timeout = item.Timeout != TimeSpan.Zero ? item.Timeout : defaultTimeout;

                var httpClient = _httpClientFactory();

                using var requestMessage = new HttpRequestMessage(method, item.Uri);

#if NET5_0_OR_GREATER
                requestMessage.Version = httpClient.DefaultRequestVersion;
                requestMessage.VersionPolicy = httpClient.DefaultVersionPolicy;
#endif

                foreach (var (Name, Value) in item.Headers)
                {
                    requestMessage.Headers.Add(Name, Value);
                }

                using (var timeoutSource = new CancellationTokenSource(timeout))
                using (var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(timeoutSource.Token, cancellationToken))
                {
                    using var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, linkedSource.Token).ConfigureAwait(false);

                    if (!((int)response.StatusCode >= Min && (int)response.StatusCode <= Max))
                    {
                        return new HealthCheckResult(context.Registration.FailureStatus, description: $"Discover endpoint #{idx} is not responding with code in {Min}...{Max} range, the current status is {response.StatusCode}.");
                    }

                    if (item.ExpectedContent != null)
                    {
                        string responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (responseBody != item.ExpectedContent)
                            return new HealthCheckResult(context.Registration.FailureStatus, description: $"The expected value '{item.ExpectedContent}' was not found in the response body.");
                    }

                    ++idx;
                }
            }
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, exception: ex);
        }
    }
}
