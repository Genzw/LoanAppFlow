"use client";
import { useRef, useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { ApplicationForm, ApplicationResult, emptyForm, useApplicationSession } from "../../features/applications/ApplicationSession";

const fields: { key: keyof ApplicationForm; label: string; max: number; placeholder: string }[] = [
  { key: "firstName", label: "First Name", max: 100, placeholder: "Jane" },
  { key: "lastName", label: "Last Name", max: 100, placeholder: "Doe" },
  { key: "companyName", label: "Company / Business Name", max: 200, placeholder: "Acme Enterprises LLC" },
  { key: "line1", label: "Street Address", max: 200, placeholder: "100 Market St" },
  { key: "line2", label: "Suite / Apt / Unit (Optional)", max: 200, placeholder: "Suite 400" },
  { key: "city", label: "City", max: 100, placeholder: "San Francisco" },
  { key: "state", label: "State Code (2 letters)", max: 2, placeholder: "CA" },
  { key: "postalCode", label: "ZIP / Postal Code", max: 20, placeholder: "94105" },
  { key: "ssn", label: "SSN (Demo / Fictitious)", max: 11, placeholder: "000-00-0003" },
  { key: "requestedAmount", label: "Requested Amount (USD)", max: 12, placeholder: "5000.00" }
];

function validate(form: ApplicationForm) {
  const errors: Record<string, string> = {};
  for (const field of fields) {
    const value = form[field.key].trim();
    if (field.key !== "line2" && !value) {
      errors[field.key] = "This field is required.";
    } else if (value.length > field.max) {
      errors[field.key] = `Maximum length is ${field.max} characters.`;
    }
  }
  if (!/^[a-zA-Z]{2}$/.test(form.state.trim())) {
    errors.state = "Please enter a 2-letter state abbreviation (e.g. CA, NY).";
  }
  if (!/^(?:[0-9]{9}|[0-9]{3}-[0-9]{2}-[0-9]{4})$/.test(form.ssn.trim())) {
    errors.ssn = "Must be 9 digits or standard XXX-XX-XXXX format.";
  }
  if (!/^\d+(?:\.\d{1,2})?$/.test(form.requestedAmount) || Number(form.requestedAmount) <= 0 || Number(form.requestedAmount) > 999999999.99) {
    errors.requestedAmount = "Must be a valid positive amount between 0.01 and 999,999,999.99.";
  }
  return errors;
}

export default function ApplyPage() {
  const { form, setForm, setResult } = useApplicationSession();
  const router = useRouter();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [message, setMessage] = useState("");
  const [busy, setBusy] = useState(false);
  const [showSsn, setShowSsn] = useState(false);
  const submitting = useRef(false);

  function focusFirst(next: Record<string, string>) {
    document.getElementById(Object.keys(next)[0])?.focus();
  }

  function loadApprovedPreset() {
    setForm({
      firstName: "Alex",
      lastName: "Morgan",
      companyName: "Pacific Coast Technologies",
      line1: "500 Howard Street",
      line2: "Floor 3",
      city: "San Francisco",
      state: "CA",
      postalCode: "94105",
      ssn: "000-00-0003",
      requestedAmount: "5000.00"
    });
    setErrors({});
    setMessage("");
  }

  function loadDeniedPreset() {
    setForm({
      firstName: "Jordan",
      lastName: "Taylor",
      companyName: "Empire State Holdings",
      line1: "350 5th Avenue",
      line2: "",
      city: "New York",
      state: "NY",
      postalCode: "10118",
      ssn: "000-00-0002",
      requestedAmount: "5000.00"
    });
    setErrors({});
    setMessage("");
  }

  return (
    <main className="workspace">
      <header>
        <Link className="brand" href="/">LoanAppFlow</Link>
        <nav>
          <Link href="/">Home</Link>
          <Link href="/admin/rules">Policy Rules</Link>
          <Link href="/admin/applications">Deliveries</Link>
          <Link href="/admin/activity">Activity</Link>
          <span className="badge">Instant Evaluation</span>
        </nav>
      </header>

      <div className="workspace-heading">
        <span className="eyebrow">Origination Portal</span>
        <h1>Loan Application</h1>
        <p className="description">
          Submit applicant information and desired funding amount. All submissions are evaluated in real time against the active risk policy.
        </p>
      </div>

      <div style={{ display: "flex", gap: "12px", marginBottom: "20px", flexWrap: "wrap" }}>
        <button type="button" className="secondary" onClick={loadApprovedPreset} style={{ fontSize: "12px", padding: "8px 14px" }}>
          ⚡ Load Sample Approved Case (CA, $5,000)
        </button>
        <button type="button" className="secondary" onClick={loadDeniedPreset} style={{ fontSize: "12px", padding: "8px 14px" }}>
          ⚡ Load Sample Denied Case (NY Rule)
        </button>
      </div>

      <form className="panel" noValidate onSubmit={async e => {
        e.preventDefault();
        if (submitting.current) return;
        const invalid = validate(form);
        setErrors(invalid);
        setMessage("");
        setResult(null);
        if (Object.keys(invalid).length) {
          setMessage("Please review the highlighted fields before submitting.");
          focusFirst(invalid);
          return;
        }
        submitting.current = true;
        setBusy(true);
        let navigating = false;
        try {
          const response = await fetch("/api/applications", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
              firstName: form.firstName,
              lastName: form.lastName,
              companyName: form.companyName,
              ssn: form.ssn,
              requestedAmount: Number(form.requestedAmount),
              address: {
                line1: form.line1,
                line2: form.line2 || null,
                city: form.city,
                state: form.state,
                postalCode: form.postalCode
              }
            })
          });
          const data = await response.json();
          if (!response.ok) {
            if (response.status === 422 && data.errors) {
              const next: Record<string, string> = {};
              for (const [key, value] of Object.entries(data.errors)) {
                next[key.replace("address.", "")] = (value as string[]).join(" ");
              }
              setErrors(next);
              setMessage("Please verify the provided input data.");
              requestAnimationFrame(() => focusFirst(next));
            } else if (data.code === "POLICY_UNAVAILABLE") {
              setMessage("No active credit policy is currently published. Please try again once initialized.");
            } else {
              setMessage("Could not confirm result. Your data is preserved in memory. Please check status before re-submitting.");
            }
            return;
          }
          if (data.decision !== "Approved" && data.decision !== "Denied") throw new Error("Invalid response from decision engine");
          setResult(data as ApplicationResult);
          if (data.decision === "Approved") setForm(emptyForm);
          navigating = true;
          router.push(data.decision === "Approved" ? "/approved" : "/denied");
        } catch {
          setMessage("Could not confirm result. Your form data is preserved in memory; automatic resubmission was prevented.");
        } finally {
          if (!navigating) {
            submitting.current = false;
            setBusy(false);
          }
        }
      }}>
        {message && <div className="notice error" role="alert">{message}</div>}

        <fieldset disabled={busy}>
          <legend className="sr-only">Applicant Information</legend>
          <div className="form-grid">
            {fields.map(field => (
              <div key={field.key}>
                <label htmlFor={field.key}>
                  {field.label}
                  <input
                    id={field.key}
                    autoComplete="off"
                    value={form[field.key]}
                    placeholder={field.placeholder}
                    type={field.key === "ssn" && !showSsn ? "password" : "text"}
                    inputMode={field.key === "requestedAmount" ? "decimal" : undefined}
                    aria-invalid={!!errors[field.key]}
                    aria-describedby={errors[field.key] ? `${field.key}-error` : undefined}
                    onChange={e => setForm({ ...form, [field.key]: e.target.value })}
                    onBlur={() => setErrors(current => ({ ...current, [field.key]: validate(form)[field.key] || "" }))}
                  />
                </label>
                {errors[field.key] && (
                  <span id={`${field.key}-error`} className="field-error">
                    {errors[field.key]}
                  </span>
                )}
              </div>
            ))}
          </div>

          <label className="check">
            <input
              type="checkbox"
              checked={showSsn}
              onChange={e => setShowSsn(e.target.checked)}
            />
            Show SSN characters
          </label>

          <div className="button-row">
            <button type="submit" disabled={busy}>
              {busy ? "Evaluating Application..." : "Submit Loan Application"}
            </button>
          </div>
        </fieldset>
      </form>
    </main>
  );
}
