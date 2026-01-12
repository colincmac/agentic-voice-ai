import {
  CopilotRuntime,
  ExperimentalEmptyAdapter,
  copilotRuntimeNextJSAppRouterEndpoint,
} from "@copilotkit/runtime";
import { HttpAgent  } from "@ag-ui/client";
import { NextRequest } from "next/server";


// FamilyAI AG-UI backend URL - defaults to localhost:8000 for development
const FAMILY_AI_AGUI_URL =
  process.env.FAMILY_AI_AGUI_URL || "http://localhost:5172/agent";

// 1. You can use any service adapter here for multi-agent support. We use
//    the empty adapter since we're only using one agent.
const serviceAdapter = new ExperimentalEmptyAdapter();

// 2. Create the CopilotRuntime instance and utilize the Microsoft Agent Framework
// AG-UI integration to setup the connection to the FamilyAI backend.
const runtime = new CopilotRuntime({

  agents: {
    // FamilyAI AG-UI endpoint - connects to the Showcase.Agent.FamilyAI ASP.NET Core app
    family_historian: new HttpAgent({
        url: FAMILY_AI_AGUI_URL,

    }),
  },
});

// 3. Build a Next.js API route that handles the CopilotKit runtime requests.
export const POST = async (req: NextRequest) => {

  const { handleRequest } = copilotRuntimeNextJSAppRouterEndpoint({
    runtime,
    serviceAdapter,
    endpoint: "/api/copilotkit",
  });
  return await handleRequest(req);
};
