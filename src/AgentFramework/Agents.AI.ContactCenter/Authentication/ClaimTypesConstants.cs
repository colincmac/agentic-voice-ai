using System;
using System.Collections.Generic;
using System.Text;

namespace Agents.AI.ContactCenter.Authentication;

public static class ClaimTypesConstants
{
    public static class Jwt
    {
        public const string Subject = "sub";
        public const string Name = "name";
        public const string Email = "email";
        public const string PhoneNumber = "phone_number";
        public const string AuthenticationMethod = "amr";
        public const string AuthenticationContextClassReference = "acr";

    }
}
