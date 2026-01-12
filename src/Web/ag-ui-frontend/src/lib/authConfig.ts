import { Configuration, LogLevel, RedirectRequest } from "@azure/msal-browser";

export const msalConfig: Configuration = {
  auth: {
    clientId: process.env.NEXT_PUBLIC_AAD_FRONTEND_CLIENT_ID!, // SPA app registration
    authority: `https://login.microsoftonline.com/${process.env.NEXT_PUBLIC_AAD_TENANT_ID}`,
    redirectUri: process.env.NEXT_PUBLIC_REDIRECT_URI || "http://localhost:3000",
  },
  cache: {
    cacheLocation: "sessionStorage",
    storeAuthStateInCookie: false,
  },
  system: {
    allowPlatformBroker: false, // Disables WAM Broker
    loggerOptions: {
      loggerCallback(level, message, containsPii) {
        if (containsPii) return;

        if (level === LogLevel.Error) {
          console.error(message);
        } else if (level === LogLevel.Info) {
          console.info(message);
        } else if (level === LogLevel.Verbose) {
          console.debug(message);
        } else if (level === LogLevel.Warning) {
          console.warn(message);
        }
      },
    },
  },
};

export const protectedResource = {
  familyApi: {
    // endpoint: process.env.NEXT_PUBLIC_FAMILY_AI_API_URL || "http://localhost:8000",
    scopes: [process.env.NEXT_PUBLIC_FAMILY_API_SCOPE!], // e.g. "api://BACKEND_CLIENT_ID/FamilyAI.Access"
  } as RedirectRequest,
};
