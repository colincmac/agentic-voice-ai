namespace Showcase.Authentication.Core;
public static class ProtectedResourceConstants
{
    public const string DefaultOAuthProtectedResourcePathSuffix = "/.well-known/oauth-protected-resource";

    public const string JsonWebKeySetPathSuffix = "/.well-known/jwks";

    public static class WWWAuthenticateKeys
    {
        public const string UnsignedResourceMetadata = "resource_metadata";
        public const string SignedResourceMetadata = "signed_metadata";
    }
    
    public static class AuthenticationSchemes
    {
        public const string DPoP = "DPoP";
    }
    
    public static class ErrorCodes
    {
        public const string InvalidToken = "invalid_token";
        public const string InsufficientScope = "insufficient_scope";
        public const string UseDPoPNonce = "use_dpop_nonce";
        public const string InvalidDPoPProof = "invalid_dpop_proof";
        public const string InvalidAuthorizationDetails = "invalid_authorization_details";
    }
    
    public static class DPoPConstants
    {
        public const string HeaderName = "DPoP";
        public const string TokenType = "dpop+jwt";
        public const string ConfirmationClaim = "cnf";
        public const string JwkThumbprintClaim = "jkt";
        public const string HttpMethodClaim = "htm";
        public const string HttpUriClaim = "htu";
        public const string JwtIdClaim = "jti";
        public const string IssuedAtClaim = "iat";
        
        public static class SupportedAlgorithms
        {
            public const string RS256 = "RS256";
            public const string RS384 = "RS384";
            public const string RS512 = "RS512";
            public const string ES256 = "ES256";
            public const string ES384 = "ES384";
            public const string ES512 = "ES512";
            public const string PS256 = "PS256";
            public const string PS384 = "PS384";
            public const string PS512 = "PS512";
        }
    }
    
    public static class AuthorizationDetailsConstants
    {
        public const string ParameterName = "authorization_details";
        public const string TypeField = "type";
        public const string LocationsField = "locations";
        public const string ActionsField = "actions";
        public const string DatatypesField = "datatypes";
        public const string IdentifierField = "identifier";
    }
    
    public static class BearerTokenMethods
    {
        public const string Header = "header";
        public const string Body = "body";
        public const string Query = "query";
    }
}
