"use client";

import { ReactNode, useEffect, useState } from "react";
import { CopilotKit } from "@copilotkit/react-core";
import { MsalProvider } from "@azure/msal-react";

import { useFamilyApiToken } from "@/lib/useApiToken";

import { msalInstance, msalInitialization } from "@/lib/msalClient";

msalInitialization.catch((error) => {
  console.error("MSAL failed to initialize", error);
});

type MsalCopilotProviderProps = {
  children: ReactNode;
};

export function MsalCopilotProvider({ children }: MsalCopilotProviderProps) {
  return (
    <MsalProvider instance={msalInstance}>
      <CopilotAuthBridge>{children}</CopilotAuthBridge>
    </MsalProvider>
  );
}
// export function MsalCopilotProvider({ children }: MsalCopilotProviderProps) {
//   return (
//     <CopilotKit
//       runtimeUrl="/api/copilotkit"
//       agent="family_historian"
//     >
//       {children}
//     </CopilotKit>
//   );
// }
function CopilotAuthBridge({ children }: MsalCopilotProviderProps) {
  const { getToken } = useFamilyApiToken();
  const [authToken, setAuthToken] = useState<string>();

  const [copilotHeaders, setCopilotHeaders] = useState<Record<string, string>>();
  const [authError, setAuthError] = useState<string | null>(null);

  useEffect(() => {
    let isMounted = true;

    const acquireToken = async () => {
      try {
        const response = await getToken();

        if (!isMounted || !response?.accessToken) {
          return;
        }
        setAuthToken(response.accessToken);
        setCopilotHeaders({
          Authorization: `Bearer ${response.accessToken}`,
        });
      } catch (error) {
        console.error("Failed to acquire token for CopilotKit", error);
        if (isMounted) {
          setAuthError(
            "We couldn't sign you in automatically. Please refresh the page and try again."
          );
        }
      }
    };

    acquireToken();

    return () => {
      isMounted = false;
    };
  }, [getToken]);

  if (authError) {
    return (
      <div role="alert" className="p-4 text-sm text-red-700">
        {authError}
      </div>
    );
  }

  if (!copilotHeaders) {
    return (
      <div className="flex min-h-screen items-center justify-center text-sm text-neutral-600">
        Signing you in...
      </div>
    );
  }

  return (
    <CopilotKit
      runtimeUrl="/api/copilotkit"
      agent="family_historian"
      headers={copilotHeaders}
      properties={{ authorization: authToken }}
    >
      {children}
    </CopilotKit>
  );
}
