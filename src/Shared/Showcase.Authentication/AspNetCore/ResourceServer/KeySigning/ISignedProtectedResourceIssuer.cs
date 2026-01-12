using Showcase.Authentication.Core;

namespace Showcase.Authentication.AspNetCore.ResourceServer.KeySigning;
public interface ISignedProtectedResourceIssuer
{

    Task<JwksDocument> GetJwksDocumentAsync(CancellationToken cancellationToken = default);

    Task<string> GetSignMetadataTokenAsync(ProtectedResourceMetadata metadata, CancellationToken cancellationToken = default);
}
