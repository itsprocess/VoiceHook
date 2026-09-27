import net from 'node:net';
import os from 'node:os';
import { randomUUID } from 'node:crypto';

export const pipeName = () => `\\\\.\\pipe\\VoiceHook-${os.userInfo().username}`;

export function command(payload, pipe = pipeName()) {
  return new Promise((resolve, reject) => {
    const socket = net.createConnection(pipe);
    let buffer = '';
    const fail = error => { socket.destroy(); reject(error); };
    socket.setTimeout(2500, () => fail(new Error('VoiceHook did not respond.')));
    socket.on('error', fail);
    socket.on('connect', () => socket.write(JSON.stringify(payload) + '\n'));
    socket.on('data', data => {
      buffer += data.toString('utf8');
      if (buffer.length > 16384) return fail(new Error('Invalid VoiceHook reply.'));
      if (!buffer.includes('\n')) return;
      try { const reply = JSON.parse(buffer.split('\n')[0]); socket.end(); resolve(reply); }
      catch { fail(new Error('Invalid VoiceHook reply.')); }
    });
    socket.on('end', () => { if (!buffer.includes('\n')) fail(new Error('VoiceHook closed the connection.')); });
  });
}

// Serialize each key's press/release, including very short taps. No audio lives here.
export class PushToTalk {
  constructor(send = command, display = () => {}) { this.send = send; this.display = display; this.active = new Map(); this.chains = new Map(); }
  enqueue(context, action, recordingId) {
    const previous = this.chains.get(context) ?? Promise.resolve();
    const next = previous.catch(() => {}).then(async () => {
      try {
        const reply = await this.send({ action, recordingId });
        this.display(context, reply.ok ? (reply.state === 'recording' ? 'Listening' : 'Hold to talk') : 'Busy', !reply.ok);
      } catch { this.display(context, 'Open\nVoiceHook', true); }
    });
    this.chains.set(context, next);
    next.finally(() => { if (this.chains.get(context) === next) this.chains.delete(context); });
    return next;
  }
  handle(event, context) {
    if (event === 'keyDown') {
      if (this.active.has(context)) return this.chains.get(context);
      const id = randomUUID(); this.active.set(context, id);
      return this.enqueue(context, 'start', id);
    }
    if (event === 'keyUp' || event === 'willDisappear') {
      const id = this.active.get(context); if (!id) return;
      this.active.delete(context);
      return this.enqueue(context, event === 'keyUp' ? 'stop' : 'cancel', id);
    }
  }
  async cancelAll() { await Promise.all([...this.active.keys()].map(context => this.handle('willDisappear', context))); }
}
