import { HttpAgent, type HttpAgentConfig, type RunAgentInput } from "@ag-ui/client";

import { protectedResource } from "@/lib/authConfig";
import { msalInitialization, msalInstance } from "@/lib/msalClient";

export class MsalHttpAgent extends HttpAgent {
  private readonly scopes: string[];

  constructor(config: HttpAgentConfig, scopes: string[] = protectedResource.familyApi.scopes) {
    super(config);
    this.scopes = scopes;
  }

  async runAgent(
    parameters?: Parameters<HttpAgent["runAgent"]>[0],
    subscriber?: Parameters<HttpAgent["runAgent"]>[1]
  ) {
    await this.applyAuthorizationHeader();
    return super.runAgent(parameters, subscriber);
  }

  /**
   * Returns the fetch config for the http request.
   * Override this to customize the request.
   */
  protected requestInit(input: RunAgentInput): RequestInit {
    return {
      method: "POST",
      headers: {
        ...this.headers,
        "Content-Type": "application/json",
        Accept: "text/event-stream",
      },
      body: JSON.stringify(input),
      signal: this.abortController.signal,
    };
  }

  private async applyAuthorizationHeader() {
    const accessToken = await this.acquireAccessToken();
    this.headers = {
      ...this.headers,
      Authorization: `Bearer ${accessToken}`,
    };
  }

  private async acquireAccessToken(): Promise<string> {
    await msalInitialization;

    const account = msalInstance.getActiveAccount() ?? msalInstance.getAllAccounts()[0];

    if (!account) {
      throw new Error("No authenticated MSAL account is available for AG-UI requests.");
    }

    const response = await msalInstance.acquireTokenSilent({
      account,
      scopes: this.scopes,
    });

    if (!response?.accessToken) {
      throw new Error("MSAL did not return an access token for AG-UI requests.");
    }

    return response.accessToken;
  }
}
