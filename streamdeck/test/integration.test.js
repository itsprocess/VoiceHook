import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { fileURLToPath } from 'node:url';
import { WebSocketServer } from 'ws';

test('SDK key -> packaged plugin -> native pipe -> Windows transcription -> HTTP hook', {timeout:20000}, async () => {
  let resolveHook;
  const delivered=new Promise(r=>resolveHook=r);
  const hook=http.createServer(async (req,res)=>{let body='';for await(const chunk of req)body+=chunk;res.writeHead(204).end();resolveHook({body:JSON.parse(body),key:req.headers['idempotency-key']});});
  await new Promise(r=>hook.listen(0,'127.0.0.1',r));
  const wss=new WebSocketServer({host:'127.0.0.1',port:0});await once(wss,'listening');
  const dll=fileURLToPath(new URL('../../tests/VoiceHook.Tests/bin/Debug/net8.0-windows/VoiceHook.Tests.dll',import.meta.url));
  const native=spawn('dotnet',[dll,'--bridge',`http://127.0.0.1:${hook.address().port}/transcripts`],{windowsHide:true});
  let plugin;
  try {
    await Promise.race([new Promise((resolve,reject)=>{let text='';native.stdout.on('data',b=>{text+=b;if(text.includes('BRIDGE_READY'))resolve();});native.once('error',reject);native.once('exit',code=>reject(new Error(`Native host exited ${code}`)));}),new Promise((_,reject)=>setTimeout(()=>reject(new Error('Native host readiness timeout')),5000).unref())]);
    const connected=once(wss,'connection',{signal:AbortSignal.timeout(5000)});
    plugin=spawn(process.execPath,[fileURLToPath(new URL('../com.voicehook.ptt.sdPlugin/bin/plugin.js',import.meta.url)),'-port',String(wss.address().port),'-pluginUUID','test-plugin','-registerEvent','registerPlugin','-info','{}'],{windowsHide:true});
    const [socket]=await connected;
    const [registration]=await once(socket,'message',{signal:AbortSignal.timeout(5000)});assert.equal(JSON.parse(registration).event,'registerPlugin');
    const send=event=>socket.send(JSON.stringify({event,action:'com.voicehook.ptt.hold',context:'key1',payload:{}}));
    send('keyDown');
    const [listening]=await once(socket,'message',{signal:AbortSignal.timeout(5000)});assert.equal(JSON.parse(listening).payload.title,'Listening');
    send('keyUp');const result=await Promise.race([delivered,new Promise((_,reject)=>setTimeout(()=>reject(new Error('Webhook timeout')),6000).unref())]);
    assert.equal(result.body.event,'transcript.completed');assert.equal(result.body.source,'streamdeck');assert.equal(result.body.provider,'windows');assert.ok(result.body.text.length>0);assert.equal(result.key,result.body.id);
  } finally {
    plugin?.kill();native.kill();for(const c of wss.clients)c.terminate();await new Promise(r=>wss.close(r));await new Promise(r=>hook.close(r));
  }
});
