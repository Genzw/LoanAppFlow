import type { Metadata } from "next";
import "./styles.css";
import "./policies.css";
import { ApplicationSession } from "../features/applications/ApplicationSession";
import SessionControl from "../features/SessionControl";
export const metadata: Metadata = {
  title: "LoanAppFlow · Decision & Origination Platform",
  description: "Distributed enterprise loan origination and risk policy engine"
};
export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        <ApplicationSession>
          <SessionControl />
          {children}
        </ApplicationSession>
      </body>
    </html>
  );
}
