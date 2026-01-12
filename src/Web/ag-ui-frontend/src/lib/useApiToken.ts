"use client";

import { useCallback } from "react";
import { useMsal } from "@azure/msal-react";
import { protectedResource } from "./authConfig";

const familyApiScopes = protectedResource.familyApi.scopes;

export function useFamilyApiToken() {
  const { instance, accounts } = useMsal();

  const getToken = useCallback(async () => {
    let account = accounts[0];

    if (!account) {
      const loginResponse = await instance.loginPopup({
        scopes: familyApiScopes,
      });
      account = loginResponse.account || undefined;
    }

    if (!account) {
      throw new Error("Unable to acquire account information for token request.");
    }

    try {
      return await instance.acquireTokenSilent({
        account,
        scopes: familyApiScopes,
      });
    } catch {
      return instance.acquireTokenPopup({
        account,
        scopes: familyApiScopes,
      });
    }
  }, [accounts, instance]);

  return { getToken };
}
