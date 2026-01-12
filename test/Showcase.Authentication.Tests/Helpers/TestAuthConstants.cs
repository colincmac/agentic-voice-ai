using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Showcase.Authentication.AspNetCore.ResourceServer.Authentication;
using Showcase.Authentication.Core;

namespace Showcase.Authentication.Tests.Helpers;
internal class TestAuthConstants
{
    public static ProtectedResourceOptions MinimalOptions =>
        new ProtectedResourceOptions
        {

        };

    public const string HttpLocalHost = "https://localhost";
    public const string ApiClientId = "1EE5A092-0DFD-42B6-88E5-C517C0141321";
    public const string ApiAudience = "api://" + ApiClientId;


    public const string UserOne = "User One";
    public const string UserTwo = "User Two";

    public const string ClientId = "87f0ee88-8251-48b3-8825-e0c9563f5234";
    public const string TenantIdAsGuid = "f645ad92-e38d-4d1a-b510-d1b09a74a8ca";
    public const string AadInstance = "https://login.microsoftonline.com";
    public const string AadIssuer = AadInstance + "/" + TenantIdAsGuid + "/v2.0";
    public const string OpenIdMetadataAddress = AadIssuer + "/.well-known/openid-configuration";
    public const string AuthorityWithTenantSpecified = AadInstance + "/" + TenantIdAsGuid;


    // This value is only for testing purposes. It is for a certificate that is not used for anything other than running tests
    public const string CertificateX5c = @"MIIDHzCCAgegAwIBAgIQM6NFYNBJ9rdOiK+C91ZzFDANBgkqhkiG9w0BAQsFADAgMR4wHAYDVQQDExVBQ1MyQ2xpZW50Q2VydGlmaWNhdGUwHhcNMTIwNTIyMj
            IxMTIyWhcNMzAwNTIyMDcwMDAwWjAgMR4wHAYDVQQDExVBQ1MyQ2xpZW50Q2VydGlmaWNhdGUwggEiMA0GCSqGSIb3DQEBAQUAA4IBDwAwggEKAoIBAQCh7HjK
            YyVMDZDT64OgtcGKWxHmK2wqzi2LJb65KxGdNfObWGxh5HQtjzrgHDkACPsgyYseqxhGxHh8I/TR6wBKx/AAKuPHE8jB4hJ1W6FczPfb7FaMV9xP0qNQrbNGZU
            YbCdy7U5zIw4XrGq22l6yTqpCAh59DLufd4d7x8fCgUDV3l1ZwrncF0QrBRzns/O9Ex9pXsi2DzMa1S1PKR81D9q5QSW7LZkCgSSqI6W0b5iodx/a3RBvW3l7d
            noW2fPqkZ4iMcntGNqgsSGtbXPvUR3fFdjmg+xq9FfqWyNxShlZg4U+wE1v4+kzTJxd9sgD1V0PKgW57zyzdOmTyFPJFAgMBAAGjVTBTMFEGA1UdAQRKMEiAEM
            9qihCt+12P5FrjVMAEYjShIjAgMR4wHAYDVQQDExVBQ1MyQ2xpZW50Q2VydGlmaWNhdGWCEDOjRWDQSfa3ToivgvdWcxQwDQYJKoZIhvcNAQELBQADggEBAIm6
            gBOkSdYjXgOvcJGgE4FJkKAMQzAhkdYq5+stfUotG6vZNL3nVOOA6aELMq/ENhrJLC3rTwLOIgj4Cy+B7BxUS9GxTPphneuZCBzjvqhzP5DmLBs8l8qu10XAsh
            y1NFZmB24rMoq8C+HPOpuVLzkwBr+qcCq7ry2326auogvVMGaxhHlwSLR4Q1OhRjKs8JctCk2+5Qs1NHfawa7jWHxdAK6cLm7Rv/c0ig2Jow7wRaI5ciAcEjX7
            m1t9gRT1mNeeluL4cZa6WyVXqXc6U2wfR5DY6GOMUubN5Nr1n8Czew8TPfab4OG37BuEMNmBpqoRrRgFnDzVtItOnhuFTa0=";

    // This value is only for testing purposes. It is for a certificate that is not used for anything other than running tests and has a private key.
    public const string CertificateX5cWithPrivateKey = @"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";
    public const string CertificateX5cWithPrivateKeyPassword = "SelfSignedTestCert";

}
