import {
  AuthenticationResult,
  EventMessage,
  EventType,
  PublicClientApplication,
} from "@azure/msal-browser";

import { msalConfig } from "./authConfig";

export const msalInstance = new PublicClientApplication(msalConfig);

let initialized = false;

export const msalInitialization = msalInstance
  .initialize()
  .then(() => {
    if (initialized) {
      return;
    }

    initialized = true;

    const accounts = msalInstance.getAllAccounts();
    if (accounts.length > 0) {
      msalInstance.setActiveAccount(accounts[0]);
    }

    msalInstance.addEventCallback((event: EventMessage) => {
      if (event.eventType === EventType.LOGIN_SUCCESS && event.payload) {
        const payload = event.payload as AuthenticationResult;
        if (payload.account) {
          msalInstance.setActiveAccount(payload.account);
        }
      }
    });
  })
  .catch((error) => {
    console.error("Failed to initialize MSAL instance", error);
    throw error;
  });
