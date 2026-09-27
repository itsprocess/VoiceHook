import test from 'node:test';
import assert from 'node:assert/strict';
import net from 'node:net';
import { PushToTalk, command } from '../src/control.js';

test('short taps and repeated key-down produce one ordered recording', async () => {
  const sent = [];
  const ptt = new PushToTalk(async c => { await new Promise(r => setTimeout(r, 15)); sent.push(c); return {ok:true,state:'idle'}; });
  ptt.handle('keyDown','a'); ptt.handle('keyDown','a'); await ptt.handle('keyUp','a');
  assert.deepEqual(sent.map(c => c.action), ['start','stop']); assert.equal(sent[0].recordingId,sent[1].recordingId);
  ptt.handle('keyDown','a'); await ptt.handle('willDisappear','a');
  assert.equal(sent[3].action,'cancel'); assert.notEqual(sent[0].recordingId,sent[2].recordingId);
});
test('connection failure still releases ownership and shows an error', async () => {
  const shown=[]; const ptt=new PushToTalk(async()=>{throw new Error('offline');},(...v)=>shown.push(v));
  await ptt.handle('keyDown','a'); await ptt.handle('keyUp','a');
  assert.equal(ptt.active.size,0); assert.equal(shown.length,2); assert.equal(shown[0][2],true);
});
test('SDK disconnect cancels all held keys', async () => {
  const sent=[];const ptt=new PushToTalk(async c=>{sent.push(c);return {ok:true,state:'idle'};});
  ptt.handle('keyDown','a');ptt.handle('keyDown','b');await ptt.cancelAll();
  assert.equal(sent.filter(c=>c.action==='cancel').length,2);assert.equal(ptt.active.size,0);
});
test('named-pipe framing handles split replies', async () => {
  const pipe=`\\\\.\\pipe\\VoiceHook-test-${process.pid}`;
  const server=net.createServer(socket=>{socket.once('data',data=>{assert.equal(JSON.parse(data).action,'status');socket.write('{"ok":true,');setTimeout(()=>socket.end('"state":"idle"}\n'),10);});});
  await new Promise(resolve=>server.listen(pipe,resolve));
  try {assert.equal((await command({action:'status'},pipe)).state,'idle');}
  finally {await new Promise(resolve=>server.close(resolve));}
});
