import { test, expect } from "@playwright/test";
test("real worker delivers created and updated events to independent HTTP mock with deduplication", async ({ request }) => {
  test.skip(!process.env.TEST_E2E_MOCK_URL, "Requires isolated stack including the mock");
  const headers = { Origin: process.env.TEST_E2E_BASE_URL! };
  const form = { firstName: "Ana", lastName: "Paz", companyName: "Delivery Demo", ssn: "000000005", requestedAmount: 100,
    address: { line1: "Demo", line2: null, city: "Demo", state: "CA", postalCode: "90001" } };
  const firstResponse = await request.post("/api/applications", { headers, data: form }); expect(firstResponse.status()).toBe(200);
  const first = await firstResponse.json(); expect(first.operation).toBe("Created");
  const second = await (await request.post("/api/applications", { headers, data: { ...form, companyName: "Updated Demo", requestedAmount: 200 } })).json();
  expect(second.applicationId).toBe(first.applicationId); expect(second.applicationVersion).toBe(2);
  const mock = process.env.TEST_E2E_MOCK_URL!;
  await expect.poll(async () => {
    const response = await request.get(`${mock}/admin/receipts?limit=100`);
    if (!response.ok()) return 0;
    return (await response.json()).items.filter((r: { applicationId: string }) => r.applicationId === first.applicationId).length;
  }, { timeout: 20000 }).toBe(2);
  const payload = { schemaVersion: 1, eventId: first.eventId, operation: "Created", policyRevisionId: first.policyRevisionId, applicationVersion: 1,
    customer: { id: first.customerId, firstName: form.firstName, lastName: form.lastName, companyName: form.companyName, address: form.address, ssn: form.ssn },
    application: { id: first.applicationId, customerId: first.customerId, requestedAmount: 100.00, currency: "USD" } };
  const duplicate = await request.post(`${mock}/applications`, { data: payload }); expect(duplicate.status()).toBe(200);
  expect((await duplicate.json()).duplicate).toBe(true);
  const resources = await (await request.get(`${mock}/admin/applications?limit=100`)).json();
  const resource = resources.items.find((r: { applicationId: string }) => r.applicationId === first.applicationId);
  expect(resource.version).toBe(2); expect(resource.companyName).toBe("Updated Demo"); expect(resource.requestedAmount).toBe(200);
  expect(resource.maskedSsn).toBe("***-**-0005"); expect(JSON.stringify(resources)).not.toContain("000000005");
});
