// H5Reborn web: audio unlock. Browsers start an AudioContext suspended until the page gets a user gesture; Godot
// resumes its context on input it handles, but a gesture that happens elsewhere (the install picker, the page before
// the canvas has focus) or before the engine made its context would leave the game silent. This keeps track of every
// AudioContext the page creates and resumes them on the first (and any later) click / key / touch.
// ?audiotest additionally taps everything played to the speakers (h5audio.level(): peak level of the last 50 ms),
// so the headless smoke test can check that sound really comes out.
'use strict';
(function () {
  const Orig = window.AudioContext || window.webkitAudioContext;
  if (!Orig) return;
  const contexts = [];
  const probe = new URLSearchParams(location.search).has('audiotest');
  const taps = new Map(); // context -> AnalyserNode

  class TrackedAudioContext extends Orig {
    constructor(...args) {
      super(...args);
      contexts.push(this);
      console.log(`h5audio: context created (${this.state}, ${this.sampleRate} Hz)`);
      this.addEventListener('statechange', () => console.log(`h5audio: context ${this.state}`));
    }
  }
  window.AudioContext = TrackedAudioContext;
  if (window.webkitAudioContext) window.webkitAudioContext = TrackedAudioContext;

  const unlock = () => { for (const c of contexts) if (c.state === 'suspended') c.resume().catch(() => {}); };
  for (const ev of ['pointerdown', 'mousedown', 'keydown', 'touchend'])
    window.addEventListener(ev, unlock, { capture: true });

  if (probe) {
    const connect = AudioNode.prototype.connect;
    AudioNode.prototype.connect = function (dest, ...rest) {
      const r = connect.call(this, dest, ...rest);
      if (dest instanceof AudioDestinationNode && !(this instanceof AnalyserNode)) {
        let a = taps.get(dest.context);
        if (!a) { a = dest.context.createAnalyser(); a.fftSize = 2048; taps.set(dest.context, a); }
        connect.call(this, a);
      }
      return r;
    };
  }

  globalThis.h5audio = {
    unlock,
    states: () => contexts.map((c) => c.state),
    level: () => {
      let peak = 0;
      for (const a of taps.values()) {
        const buf = new Float32Array(a.fftSize);
        a.getFloatTimeDomainData(buf);
        for (const v of buf) peak = Math.max(peak, Math.abs(v));
      }
      return peak;
    },
  };
}());
