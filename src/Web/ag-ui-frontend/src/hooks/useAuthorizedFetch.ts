"use client";

import { useCallback } from "react";

import { useFamilyApiToken } from "@/lib/useApiToken";

export function useAuthorizedFetch() {
  const { getToken } = useFamilyApiToken();

  return useCallback(
    async (input: RequestInfo | URL, init?: RequestInit) => {
      const tokenResponse = await getToken();
      const headers = new Headers(init?.headers || {});

      if (tokenResponse?.accessToken) {
        headers.set("Authorization", `Bearer ${tokenResponse.accessToken}`);
      }

      return fetch(input, {
        ...init,
        headers,
      });
    },
    [getToken]
  );
}
