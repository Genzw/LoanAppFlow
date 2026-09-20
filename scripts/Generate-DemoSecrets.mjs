import { randomBytes, pbkdf2Sync } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

// Local artifact only. Never prints credentials and refuses to replace existing secrets.
const directory = new URL('../.local/', import.meta.url);
const destination = new URL('cloud-demo.secrets.json', directory);
const password = randomBytes(24).toString('base64url'); const salt = randomBytes(16);
const data = {
  demoPassword: password,
  DEMO_PASSWORD_HASH: `pbkdf2-sha256$600000$${salt.toString('base64')}$${pbkdf2Sync(password, salt, 600000, 32, 'sha256').toString('base64')}`,
  SESSION_SIGNING_KEY: randomBytes(32).toString('base64'),
  BACKEND_SERVICE_TOKEN: randomBytes(32).toString('base64'),
  INTEGRATION_SERVICE_TOKEN: randomBytes(32).toString('base64')
};
await mkdir(directory, { recursive: true });
await writeFile(destination, JSON.stringify(data, null, 2), { flag: 'wx', mode: 0o600 });
console.log(`Generated local secrets: ${fileURLToPath(destination)}. Existing files are never overwritten.`);
