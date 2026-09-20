import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, pbkdf2Sync, createHmac } from 'node:crypto';
import { signSession, readSession, verifyPassword, sessionSeconds, cloudConfig } from '../src/server/session.ts';

test('session rejects tampering, expiration, future issue time and key rotation', () => {
  const key = randomBytes(32).toString('base64'); const now = 100000; const token = signSession(key, now);
  assert.equal(readSession(token, key, now).exp, now + sessionSeconds);
  assert.equal(readSession(token, key, now + sessionSeconds), null);
  assert.equal(readSession(token, key, now - 1), null);
  assert.equal(readSession(token, randomBytes(32).toString('base64'), now), null);
  assert.equal(readSession(token + 'x', key, now), null);
  assert.equal(readSession('bad', key, now), null);
  const payload = Buffer.from(JSON.stringify({ v:1, id:readSession(token,key,now).id, iat:now, exp:now+999999 })).toString('base64url');
  const signed = payload + '.' + createHmac('sha256', Buffer.from(key,'base64')).update(payload).digest('base64url');
  assert.equal(readSession(signed,key,now),null);
});
test('password verification uses salted derivation and rejects invalid input/configuration', async () => {
  const salt = randomBytes(16); const password = randomBytes(20).toString('base64');
  const hash = `pbkdf2-sha256$600000$${salt.toString('base64')}$${pbkdf2Sync(password,salt,600000,32,'sha256').toString('base64')}`;
  assert.equal(await verifyPassword(password,hash),true); assert.equal(await verifyPassword('wrong',hash),false);
  assert.equal(await verifyPassword('x'.repeat(257),hash),false); assert.equal(await verifyPassword({},hash),false);
  await assert.rejects(verifyPassword(password,hash.replace('600000','1')));
});
test('cloud configuration fails closed without secrets or HTTPS', () => {
  const names=['SESSION_SIGNING_KEY','BACKEND_SERVICE_TOKEN','DEMO_PASSWORD_HASH','DEMO_ORIGIN','BACKEND_BASE_URL'];
  const original=Object.fromEntries(names.map(n=>[n,process.env[n]]));
  try {
    for(const n of names) delete process.env[n]; assert.throws(()=>cloudConfig());
    process.env.SESSION_SIGNING_KEY=randomBytes(32).toString('base64'); process.env.BACKEND_SERVICE_TOKEN=randomBytes(32).toString('base64');
    process.env.DEMO_PASSWORD_HASH=`pbkdf2-sha256$600000$${randomBytes(16).toString('base64')}$${randomBytes(32).toString('base64')}`;
    process.env.DEMO_ORIGIN='https://demo.example'; process.env.BACKEND_BASE_URL='http://backend.example'; assert.throws(()=>cloudConfig());
    process.env.BACKEND_BASE_URL='https://backend.example/path'; assert.throws(()=>cloudConfig());
    process.env.BACKEND_BASE_URL='https://backend.example'; assert.equal(cloudConfig().origin,'https://demo.example');
    process.env.BACKEND_SERVICE_TOKEN=process.env.SESSION_SIGNING_KEY; assert.throws(()=>cloudConfig());
  } finally { for(const n of names) if(original[n]===undefined) delete process.env[n]; else process.env[n]=original[n]; }
});
