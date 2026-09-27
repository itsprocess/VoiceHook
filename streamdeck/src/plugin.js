import WebSocket from 'ws';
import { PushToTalk } from './control.js';

const args = new Map();
for (let i = 2; i + 1 < process.argv.length; i += 2) args.set(process.argv[i], process.argv[i + 1]);
const port = Number(args.get('-port'));
if (!Number.isInteger(port) || port < 1 || port > 65535) throw new Error('Launch through Stream Deck.');
const ws = new WebSocket(`ws://127.0.0.1:${port}`);
const emit = value => { if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(value)); };
const ptt = new PushToTalk(undefined, (context, title, error) => {
  emit({ event: 'setTitle', context, payload: { title, target: 0 } });
  if (error) emit({ event: 'showAlert', context });
});
ws.on('open', () => emit({ event: args.get('-registerEvent'), uuid: args.get('-pluginUUID') }));
ws.on('message', raw => {
  try {
    const event = JSON.parse(raw);
    if (event.action !== 'com.voicehook.ptt.hold') return;
    if (event.event === 'willAppear') emit({ event: 'setTitle', context: event.context, payload: { title: 'Hold to talk', target: 0 } });
    ptt.handle(event.event, event.context);
  } catch { /* Ignore malformed SDK messages; never start a recording from one. */ }
});
ws.on('error', () => {});
ws.on('close', async () => { await ptt.cancelAll(); process.exit(0); });
process.on('SIGTERM', async () => { await ptt.cancelAll(); process.exit(0); });
