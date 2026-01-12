import type { Metadata } from "next";

import "./globals.css";
import "@copilotkit/react-ui/styles.css";
import { MsalCopilotProvider } from "@/components/msal-copilot-provider";

export const metadata: Metadata = {
  title: "FamilyAI - Family History Explorer",
  description:
    "Explore your family history with the help of an AI-powered family historian",
};
export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  return (
    <html lang="en">
      <body className={"antialiased"}>
        <MsalCopilotProvider>{children}</MsalCopilotProvider>
      </body>
    </html>
  );
}
