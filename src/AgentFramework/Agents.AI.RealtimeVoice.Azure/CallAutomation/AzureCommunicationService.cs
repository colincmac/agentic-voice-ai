using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agents.AI.RealtimeVoice.Azure.Calling.CallAutomation;
using Agents.AI.RealtimeVoice.Azure.Configuration;
using Azure;
using Azure.Communication.CallAutomation;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agents.AI.RealtimeVoice.Azure.CallAutomation;


public class AzureCommunicationService
{
    private readonly CommunicationOptions _options;
    private readonly ILogger<AzureCommunicationService> _logger;
    private readonly HttpPipeline _pipeline;
    private AcsConnectionString connectionString => new(_options.Acs.ConnectionString);
    private HMACAuthenticationPolicy _hMACAuthenticationPolicy => new(connectionString.AccessKey);

    public AzureCommunicationService(IOptions<CommunicationOptions> options, ILogger<AzureCommunicationService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _pipeline = HttpPipelineBuilder.Build(new CallAutomationClientOptions(), _hMACAuthenticationPolicy);
    }
    private HttpMessage CreateGetRequest(string tenantId, string objectId)
    {
        var message = _pipeline.CreateMessage();
        var request = message.Request;
        request.Method = RequestMethod.Get;
        var uri = new RequestUriBuilder();
        uri.Reset(connectionString.Endpoint);

        uri.AppendPath("/access/teamsExtension/tenants/", false);
        uri.AppendPath(tenantId, true);
        uri.AppendPath("/assignments/", false);
        uri.AppendPath(objectId, true);
        uri.AppendQuery("api-version", _options.Acs.AcsApiVersion, true);
        request.Uri = uri;
        request.Headers.Add("Accept", "application/json");
        return message;
    }
    private HttpMessage CreateUpsertRequest(string tenantId, string objectId, TeamsExtensionPrincipalType principalType, IEnumerable<string>? clientIds = null)
    {
        var message = _pipeline.CreateMessage();
        var request = message.Request;
        request.Method = RequestMethod.Put;
        var uri = new RequestUriBuilder();
        uri.Reset(connectionString.Endpoint);
        uri.AppendPath("/access/teamsExtension/tenants/", false);
        uri.AppendPath(tenantId, true);
        uri.AppendPath("/assignments/", false);
        uri.AppendPath(objectId, true);
        uri.AppendQuery("api-version", _options.Acs.AcsApiVersion, true);
        request.Uri = uri;
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Content-Type", "application/json");
        var teamsExtensionAssignmentCreateOrUpdateRequest = new TeamsExtensionAssignmentCreateOrUpdateRequest(principalType);
        if (clientIds is not null)
        {
            foreach (var value in clientIds)
            {
                teamsExtensionAssignmentCreateOrUpdateRequest.ClientIds.Add(value);
            }
        }
        var model = teamsExtensionAssignmentCreateOrUpdateRequest;
        var content = new Utf8JsonRequestContent();
        content.SerializeWithContext(model);
        request.Content = content;
        return message;
    }

    public async Task<TeamsExtensionAssignmentResponse?> AddConfiguredTeamsResourceAccessAsync(CancellationToken cancellationToken)
    {
        using var message = CreateUpsertRequest(_options.Teams.ResourceTenantId, _options.Teams.ResourceObjectId, TeamsExtensionPrincipalType.TeamsResourceAccount);
        await _pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (message.Response.IsError || message.Response.ContentStream is null)
        {
            _logger.LogError("Failed to add Teams resource access policy to ACS resource. Status Code: {StatusCode}, Response: {Response}", message.Response.Status, message.Response.ReasonPhrase);
            return null;
        }

        return await JsonSerializer.DeserializeAsync(message.Response.ContentStream, CallAutomationJsonContext.Default.TeamsExtensionAssignmentResponse, cancellationToken).ConfigureAwait(false);
    }
    internal class AcsConnectionString
    {
        public Uri Endpoint { get; }
        public AzureKeyCredential AccessKey { get; }
        public AcsConnectionString(string connectionString)
        {
            var pairs = ParseConnectionString(connectionString);
            if (!pairs.TryGetValue("endpoint", out var endpoint))
            {
                throw new InvalidOperationException("Connection string is missing 'endpoint' keyword.");
            }
            if (!pairs.TryGetValue("accesskey", out var accessKey))
            {
                throw new InvalidOperationException("Connection string is missing 'accesskey' keyword.");
            }
            Endpoint = new Uri(endpoint);
            AccessKey = new AzureKeyCredential(accessKey);
        }
        private static Dictionary<string, string> ParseConnectionString(in string connectionString, in string separator = ";", in string keywordValueSeparator = "=")
        {
            var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var segmentStart = -1;
            var segmentEnd = 0;

            while (TryGetNextSegment(connectionString, separator, ref segmentStart, ref segmentEnd))
            {
                var kvSeparatorIndex = connectionString.IndexOf(keywordValueSeparator, segmentStart, segmentEnd - segmentStart, StringComparison.Ordinal);
                int keywordStart = GetStart(connectionString, segmentStart);
                int keyLength = GetLength(connectionString, keywordStart, kvSeparatorIndex);

                var keyword = connectionString.Substring(keywordStart, keyLength);
                if (pairs.ContainsKey(keyword))
                {
                    throw new InvalidOperationException($"Duplicated keyword '{keyword}'");
                }

                var valueStart = GetStart(connectionString, kvSeparatorIndex + keywordValueSeparator.Length);
                var valueLength = GetLength(connectionString, valueStart, segmentEnd);
                pairs.Add(keyword, connectionString.Substring(valueStart, valueLength));
            }

            return pairs;

            static int GetStart(in string str, int start)
            {
                while (start < str.Length && char.IsWhiteSpace(str[start]))
                {
                    start++;
                }

                return start;
            }

            static int GetLength(in string str, in int start, int end)
            {
                while (end > start && char.IsWhiteSpace(str[end - 1]))
                {
                    end--;
                }

                return end - start;
            }
        }

        private static bool TryGetNextSegment(in string str, in string separator, ref int start, ref int end)
        {
            if (start == -1)
            {
                start = 0;
            }
            else
            {
                start = end + separator.Length;
                if (start >= str.Length)
                {
                    return false;
                }
            }

            end = str.IndexOf(separator, start, StringComparison.Ordinal);
            if (end == -1)
            {
                end = str.Length;
            }

            return true;
        }
    }
    internal class Utf8JsonRequestContent : RequestContent
    {
        private readonly MemoryStream _stream;
        private readonly RequestContent _content;

        public Utf8JsonWriter JsonWriter { get; }

        public Utf8JsonRequestContent()
        {
            _stream = new MemoryStream();
            _content = Create(_stream);
            JsonWriter = new Utf8JsonWriter(_stream);
        }

        public Utf8JsonRequestContent SerializeWithContext<T>(T value)
        {
            JsonSerializer.Serialize(JsonWriter, value, CallAutomationJsonContext.Default.Options);
            // Do not flush here; Utf8JsonRequestContent does it in WriteTo / WriteToAsync.
            return this;
        }

        public override async Task WriteToAsync(Stream stream, CancellationToken cancellation)
        {
            await JsonWriter.FlushAsync(cancellation).ConfigureAwait(false);
            await _content.WriteToAsync(stream, cancellation).ConfigureAwait(false);
        }

        public override void WriteTo(Stream stream, CancellationToken cancellation)
        {
            JsonWriter.Flush();
            _content.WriteTo(stream, cancellation);
        }

        public override bool TryComputeLength(out long length)
        {
            length = JsonWriter.BytesCommitted + JsonWriter.BytesPending;
            return true;
        }

        public override void Dispose()
        {
            JsonWriter.Dispose();
            _content.Dispose();
            _stream.Dispose();
        }
    }

    internal class HMACAuthenticationPolicy : HttpPipelinePolicy
    {
        private const string DATE_HEADER_NAME = "x-ms-date";
        private readonly AzureKeyCredential _keyCredential;

        public HMACAuthenticationPolicy(AzureKeyCredential keyCredential)
            => _keyCredential = keyCredential;

        public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var contentHash = CreateContentHash(message);
            AddHeaders(message, contentHash);
            ProcessNext(message, pipeline);
        }

        public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
        {
            var contentHash = await CreateContentHashAsync(message).ConfigureAwait(false);
            AddHeaders(message, contentHash);
            await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        }

        private static string CreateContentHash(HttpMessage message)
        {
            var alg = SHA256.Create();

            using (var memoryStream = new MemoryStream())
            using (var contentHashStream = new CryptoStream(memoryStream, alg, CryptoStreamMode.Write))
            {
                message.Request.Content?.WriteTo(contentHashStream, message.CancellationToken);
            }

            return Convert.ToBase64String(alg.Hash!);
        }

        private static async ValueTask<string> CreateContentHashAsync(HttpMessage message)
        {
            var alg = SHA256.Create();

            using (var memoryStream = new MemoryStream())
            using (var contentHashStream = new CryptoStream(memoryStream, alg, CryptoStreamMode.Write))
            {
                if (message.Request.Content != null)
                    await message.Request.Content.WriteToAsync(contentHashStream, message.CancellationToken).ConfigureAwait(false);
            }

            return Convert.ToBase64String(alg.Hash!);
        }

        private void AddHeaders(HttpMessage message, string contentHash)
        {
            var utcNowString = DateTimeOffset.UtcNow.ToString("r", CultureInfo.InvariantCulture);
            string authorization;

            message.TryGetProperty("uriToSignRequestWith", out var uriToSignWith);
            if (uriToSignWith != null && uriToSignWith.GetType() == typeof(Uri))
            {
                authorization = GetAuthorizationHeader(message.Request.Method, (Uri)uriToSignWith, contentHash, utcNowString);
            }
            else
            {
                authorization = GetAuthorizationHeader(message.Request.Method, message.Request.Uri.ToUri(), contentHash, utcNowString);
            }

            message.Request.Headers.SetValue("x-ms-content-sha256", contentHash);
            message.Request.Headers.SetValue(DATE_HEADER_NAME, utcNowString);
            message.Request.Headers.SetValue(HttpHeader.Names.Authorization, authorization);
        }

        private string GetAuthorizationHeader(RequestMethod method, Uri uri, string contentHash, string date)
        {
            var host = uri.Authority;
            var pathAndQuery = uri.PathAndQuery;

            var stringToSign = $"{method.Method}\n{pathAndQuery}\n{date};{host};{contentHash}";
            var signature = ComputeHMAC(stringToSign);

            string signedHeaders = $"{DATE_HEADER_NAME};host;x-ms-content-sha256";
            return $"HMAC-SHA256 SignedHeaders={signedHeaders}&Signature={signature}";
        }

        private string ComputeHMAC(string value)
        {
            using var hmac = new HMACSHA256(Convert.FromBase64String(_keyCredential.Key));
            var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(value));
            return Convert.ToBase64String(hash);
        }
    }
}
