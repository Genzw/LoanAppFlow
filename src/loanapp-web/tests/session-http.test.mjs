import test from 'node:test';
import assert from 'node:assert/strict';
import { randomBytes, pbkdf2Sync } from 'node:crypto';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { readSession, signSession, sessionSeconds } from '../src/server/session.ts';

test('CloudDemo HTTP login, logout, proxy and BFF reject unauthenticated or forged requests', {timeout:60000}, async () => {
  const cwd=fileURLToPath(new URL('..',import.meta.url)); const origin='https://demo.example'; const base='http://127.0.0.1:3310';
  const key=randomBytes(32).toString('base64'); const backend=randomBytes(32).toString('base64'); const password=randomBytes(24).toString('base64'); const salt=randomBytes(16);
  const hash=`pbkdf2-sha256$600000$${salt.toString('base64')}$${pbkdf2Sync(password,salt,600000,32,'sha256').toString('base64')}`;
  const child=spawn(process.execPath,['node_modules/next/dist/bin/next','start','--hostname','127.0.0.1','--port','3310'], {cwd,windowsHide:true,env:{...process.env,APP_PROFILE:'CloudDemo',DEMO_ORIGIN:origin,SESSION_SIGNING_KEY:key,DEMO_PASSWORD_HASH:hash,BACKEND_SERVICE_TOKEN:backend,BACKEND_BASE_URL:'https://backend.invalid'},stdio:'ignore'});
  try {
    let ready=false;
    for(let n=0;n<100;n++) { if(child.exitCode!==null) throw new Error('Isolated web process exited'); try{const r=await fetch(base+'/api/session');if(r.status===401){ready=true;break;}}catch{} await new Promise(r=>setTimeout(r,200)); }
    assert.ok(ready,'isolated cloud web became ready');
    const navigation=await fetch(base+'/admin/activity',{redirect:'manual'}); assert.equal(navigation.status,307); assert.equal(navigation.headers.get('location'),origin+'/login');
    assert.equal((await fetch(base+'/api/admin/audit')).status,401);
    assert.equal((await fetch(base+'/api/admin/audit',{headers:{'X-Backend-Token':backend,'X-Demo-Session':randomBytes(16).toString('hex')}})).status,401);
    const login=(body,extra={})=>fetch(base+'/api/session',{method:'POST',headers:{Origin:origin,'Content-Type':'application/json',...extra},body:JSON.stringify(body)});
    assert.equal((await login({password},{Origin:'https://evil.example'})).status,403);
    assert.equal((await login({password:'wrong'})).status,401);
    assert.equal((await login({password:'x'.repeat(5000)})).status,413);
    const accepted=await login({password}); assert.equal(accepted.status,200);
    const cookie=accepted.headers.get('set-cookie'); for(const flag of ['HttpOnly','Secure','SameSite=strict','Path=/','Max-Age=28800']) assert.ok(cookie.includes(flag),flag);
    const pair=cookie.split(';')[0]; const token=decodeURIComponent(pair.slice(pair.indexOf('=')+1)); assert.ok(readSession(token,key));
    const state=await fetch(base+'/api/session',{headers:{Cookie:pair}}); assert.equal(state.status,200); assert.equal((await state.json()).authenticated,true);
    assert.equal((await fetch(base+'/api/admin/unknown',{headers:{Cookie:pair}})).status,404);
    assert.equal((await fetch(base+'/api/applications',{method:'POST',headers:{Cookie:pair,Origin:'https://evil.example'}})).status,403);
    assert.equal((await fetch(base+'/api/admin/audit',{headers:{Cookie:pair+'x'}})).status,401);
    const expired=signSession(key,Math.floor(Date.now()/1000)-sessionSeconds);
    assert.equal((await fetch(base+'/api/admin/audit',{headers:{Cookie:'__Host-loanapp-session='+expired}})).status,401);
    assert.equal((await fetch(base+'/api/session',{method:'DELETE',headers:{Cookie:pair,Origin:'https://evil.example'}})).status,403);
    const logout=await fetch(base+'/api/session',{method:'DELETE',headers:{Cookie:pair,Origin:origin}}); assert.equal(logout.status,200); assert.ok(logout.headers.get('set-cookie').includes('Max-Age=0'));
    assert.equal((await fetch(base+'/api/admin/audit')).status,401);
  } finally { if(child.exitCode===null) { child.kill(); await new Promise(r=>child.once('exit',r)); } }
});
