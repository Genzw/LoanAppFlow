"use client";
import { createContext, useContext, useState } from "react";

export const emptyForm = { firstName: "", lastName: "", companyName: "", line1: "", line2: "", city: "", state: "", postalCode: "", ssn: "", requestedAmount: "" };
export type ApplicationForm = typeof emptyForm;
export type ApplicationResult = { decision: "Approved"; customerId: string; applicationId: string; applicationVersion: number; operation: string; policyRevisionId: string; eventId: string }
  | { decision: "Denied"; policyRevisionId: string; reasons: { ruleId: string; code: string; message: string }[] };
const Context = createContext<{
  form: ApplicationForm; setForm: (form: ApplicationForm) => void;
  result: ApplicationResult | null; setResult: (result: ApplicationResult | null) => void;
} | null>(null);
export function ApplicationSession({ children }: { children: React.ReactNode }) {
  // Form and result exist only in this tab's memory; never persist SSN or form in storage/URLs.
  const [form, setForm] = useState(emptyForm); const [result, setResult] = useState<ApplicationResult | null>(null);
  return <Context.Provider value={{ form, setForm, result, setResult }}>{children}</Context.Provider>;
}
export function useApplicationSession() {
  const value = useContext(Context); if (!value) throw new Error("ApplicationSession required"); return value;
}
