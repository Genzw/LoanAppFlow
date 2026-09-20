"use client";
import { Condition, Field, Rule } from "./api";

const fieldLabels: Record<string, string> = {
  firstName: "First Name",
  lastName: "Last Name",
  companyName: "Company Name",
  "address.city": "City",
  "address.postalCode": "ZIP Code",
  "address.state": "State",
  requestedAmount: "Requested Amount (USD)",
  ssn: "SSN"
};

const operators: Record<string, string> = {
  equals: "Equals",
  notEquals: "Does not equal",
  in: "Is in list",
  notIn: "Is not in list",
  greaterThan: "Greater than",
  greaterThanOrEqual: "Greater than or equal to",
  lessThan: "Less than",
  lessThanOrEqual: "Less than or equal to",
  inBlacklist: "Is in Blacklist"
};

function operand(field: Field, operator: string): Condition {
  return {
    field: field.name,
    operator,
    ...(operator === "inBlacklist"
      ? {}
      : ["in", "notIn"].includes(operator)
      ? { values: [""] }
      : { value: field.type === "decimal" ? 0 : "" })
  };
}

export default function RuleEditor({
  rule,
  fields,
  onChange,
  onDelete,
  readOnly
}: {
  rule: Rule;
  fields: Field[];
  onChange: (rule: Rule) => void;
  onDelete: () => void;
  readOnly: boolean;
}) {
  function changeCondition(index: number, condition: Condition) {
    onChange({
      ...rule,
      conditions: rule.conditions.map((c, i) => (i === index ? condition : c))
    });
  }

  return (
    <article className="rule-card">
      <div className="rule-title">
        <h3>{rule.name || "Untitled Rule"}</h3>
        <span className={rule.enabled ? "badge" : "badge neutral"}>
          {rule.enabled ? "Enabled" : "Disabled"}
        </span>
      </div>

      <fieldset disabled={readOnly}>
        <legend className="sr-only">Configure {rule.name || "Rule"}</legend>
        <div className="form-grid">
          <label>
            Rule Name
            <input
              value={rule.name}
              maxLength={100}
              placeholder="e.g. Deny NY State Applicants"
              onChange={e => onChange({ ...rule, name: e.target.value })}
            />
          </label>

          <label>
            Rule Code
            <input
              value={rule.code}
              maxLength={50}
              placeholder="e.g. RULE_NY_EXCLUSION"
              onChange={e => onChange({ ...rule, code: e.target.value.toUpperCase() })}
            />
          </label>

          <label className="wide" style={{ gridColumn: "1 / -1" }}>
            Public Message for Applicant
            <input
              value={rule.publicMessage}
              maxLength={200}
              placeholder="Notice displayed if this rule is triggered"
              onChange={e => onChange({ ...rule, publicMessage: e.target.value })}
            />
          </label>

          <label>
            Evaluation Priority (Higher evaluates first)
            <input
              type="number"
              min={0}
              max={10000}
              value={rule.priority}
              onChange={e => onChange({ ...rule, priority: Number(e.target.value) })}
            />
          </label>

          <label>
            Trigger Action (Deny when)
            <select
              value={rule.match}
              onChange={e => onChange({ ...rule, match: e.target.value as Rule["match"] })}
            >
              <option value="ALL">All conditions match (AND)</option>
              <option value="ANY">Any condition matches (OR)</option>
            </select>
          </label>
        </div>

        <label className="check" style={{ marginTop: "12px" }}>
          <input
            type="checkbox"
            checked={rule.enabled}
            onChange={e => onChange({ ...rule, enabled: e.target.checked })}
          />
          Rule is active in policy
        </label>

        <div className="conditions" style={{ marginTop: "16px" }}>
          <span style={{ fontSize: "12px", fontWeight: 700, textTransform: "uppercase", color: "var(--text-muted)", letterSpacing: "0.5px" }}>
            Conditions ({rule.conditions.length})
          </span>

          {rule.conditions.map((condition, i) => {
            const field = fields.find(f => f.name === condition.field) || fields[0];
            return (
              <div
                className="condition"
                key={i}
                style={{
                  display: "grid",
                  gridTemplateColumns: "1.2fr 1.2fr 2fr auto",
                  gap: "12px",
                  alignItems: "flex-end",
                  marginTop: "8px",
                  background: "rgba(255, 255, 255, 0.02)",
                  padding: "12px",
                  borderRadius: "8px",
                  border: "1px solid var(--border-subtle)"
                }}
              >
                <label>
                  Field
                  <select
                    value={condition.field}
                    onChange={e => {
                      const next = fields.find(f => f.name === e.target.value)!;
                      changeCondition(i, operand(next, next.operators[0]));
                    }}
                  >
                    {fields.map(f => (
                      <option key={f.name} value={f.name}>
                        {fieldLabels[f.name] || f.name}
                      </option>
                    ))}
                  </select>
                </label>

                <label>
                  Operator
                  <select
                    value={condition.operator}
                    onChange={e => changeCondition(i, operand(field, e.target.value))}
                  >
                    {field.operators.map(op => (
                      <option key={op} value={op}>
                        {operators[op] || op}
                      </option>
                    ))}
                  </select>
                </label>

                {condition.operator === "inBlacklist" ? (
                  <p style={{ margin: "auto 0 10px", fontSize: "13px", color: "var(--text-secondary)" }}>
                    Evaluates against this policy&apos;s blacklist.
                  </p>
                ) : condition.values ? (
                  <label>
                    Values (one per line)
                    <textarea
                      rows={2}
                      value={condition.values.join("\n")}
                      onChange={e => changeCondition(i, { ...condition, values: e.target.value.split("\n") })}
                    />
                  </label>
                ) : (
                  <label>
                    Target Value
                    <input
                      type={field.type === "decimal" ? "number" : "text"}
                      step={field.type === "decimal" ? "0.01" : undefined}
                      min={field.type === "decimal" ? 0 : undefined}
                      max={field.type === "decimal" ? 999999999.99 : undefined}
                      maxLength={field.maxLength || undefined}
                      value={condition.value ?? ""}
                      placeholder="Comparison value..."
                      onChange={e =>
                        changeCondition(i, {
                          ...condition,
                          value: field.type === "decimal" ? Number(e.target.value) : e.target.value
                        })
                      }
                    />
                  </label>
                )}

                <button
                  className="danger"
                  type="button"
                  onClick={() => onChange({ ...rule, conditions: rule.conditions.filter((_, n) => n !== i) })}
                  aria-label={`Remove condition ${i + 1}`}
                  disabled={rule.conditions.length <= 1}
                  style={{ height: "42px", padding: "0 12px", fontSize: "12px" }}
                >
                  Remove
                </button>
              </div>
            );
          })}
        </div>

        {!readOnly && (
          <div className="button-row" style={{ marginTop: "16px" }}>
            <button
              type="button"
              className="secondary"
              disabled={rule.conditions.length >= 20}
              onClick={() => onChange({ ...rule, conditions: [...rule.conditions, operand(fields[0], fields[0].operators[0])] })}
              style={{ fontSize: "13px" }}
            >
              + Add Condition
            </button>
            <button
              type="button"
              className="danger"
              onClick={onDelete}
              style={{ fontSize: "13px" }}
            >
              Delete Rule
            </button>
          </div>
        )}
      </fieldset>
    </article>
  );
}
