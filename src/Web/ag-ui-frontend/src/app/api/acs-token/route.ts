import { CommunicationIdentityClient } from "@azure/communication-identity";
import { NextResponse } from "next/server";

/**
 * API route to generate an ACS user access token for VoIP calling.
 * This endpoint creates or reuses an ACS user and issues a token with VoIP scope.
 */
export async function POST() {
  const connectionString = process.env.AZURE_COMMUNICATION_SERVICES_CONNECTION_STRING;

  if (!connectionString) {
    return NextResponse.json(
      { error: "ACS connection string not configured" },
      { status: 500 }
    );
  }

  try {
    const identityClient = new CommunicationIdentityClient(connectionString);

    // Create a new user and token with VoIP scope
    // In production, you might want to store and reuse user identities
    const userAndToken = await identityClient.createUserAndToken(["voip"]);

    return NextResponse.json({
      userId: userAndToken.user.communicationUserId,
      token: userAndToken.token,
      expiresOn: userAndToken.expiresOn.toISOString(),
    });
  } catch (error) {
    console.error("Failed to generate ACS token:", error);
    return NextResponse.json(
      { error: "Failed to generate ACS token" },
      { status: 500 }
    );
  }
}
