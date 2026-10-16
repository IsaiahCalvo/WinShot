(() => {
  const key = Symbol.for("winshot.page-effects");
  const version = 3;
  if (window[key]?.version === version) return;
  window[key]?.dispose?.();
  let capture = null;
  const roots = () => {
    const all = [document];
    for (let i = 0; i < all.length; i++) for (const node of all[i].querySelectorAll("*")) if (node.shadowRoot) all.push(node.shadowRoot);
    return all;
  };
  function rememberMedia(node, freeze = true) {
    if (!capture.media.has(node)) capture.media.set(node, {
      paused: node.paused, time: node.currentTime, rate: node.playbackRate,
      source: node.currentSrc, stream: node.srcObject
    });
    // Capture's own pause must not overwrite the page's latest playback request.
    if (freeze && !node.paused) capture.pause.call(node);
  }
  function isLoop(animation) {
    for (let current = animation; current && current !== capture.gsap?.globalTimeline; current = current.parent) {
      const repeat = current.repeat?.();
      if (repeat < 0 || repeat === Infinity) return true;
    }
    return false;
  }
  function hasPausedParent(animation) {
    for (let parent = animation.parent; parent && parent !== capture.gsap?.globalTimeline; parent = parent.parent) {
      if (parent.paused()) return true;
    }
    return false;
  }
  function update() {
    if (!capture) return;
    capture.touched = performance.now();
    for (const root of roots()) for (const video of root.querySelectorAll("video,audio")) rememberMedia(video);
    const gsap = capture.gsap;
    if (!gsap) return;
    gsap.globalTimeline.pause();
    for (const animation of gsap.globalTimeline.getChildren(true, true, true)) {
      if (!capture.animations.has(animation)) capture.animations.set(animation, {
        time: animation.totalTime(), paused: animation.paused(), created: !capture.initial.has(animation)
      });
      if (capture.finished.has(animation) || !animation.targets || isLoop(animation) || hasPausedParent(animation)) continue;
      const vars = animation.vars || {};
      const entrance = vars.runBackwards || vars.startAt || vars.opacity !== undefined || vars.autoAlpha !== undefined;
      if (!entrance || animation.paused() || !animation.targets().some(node => node instanceof Element)) continue;
      if (Number.isFinite(animation.totalDuration()) && animation.totalDuration() > 0) {
        animation.totalProgress(1, true).pause();
        capture.finished.add(animation);
      }
    }
    // GSAP has its own ticker and does not use the Web Animations API. Sleeping
    // it preserves repeating SVG/canvas effects at one frame without killing them.
    gsap.ticker.sleep();
  }
  function restore(id) {
    if (!capture || capture.id !== id) return { restored: true };
    const saved = capture; capture = null;
    clearInterval(saved.watchdog);
    const failures = [];
    try {
      if (HTMLMediaElement.prototype.play === saved.playProxy) HTMLMediaElement.prototype.play = saved.play;
    } catch { failures.push("media playback function"); }
    try {
      if (HTMLMediaElement.prototype.pause === saved.pauseProxy) HTMLMediaElement.prototype.pause = saved.pause;
    } catch { failures.push("media pause function"); }
    if (saved.gsap) {
      try {
        try { saved.gsap.globalTimeline.totalTime(saved.rootTime, true); }
        catch { failures.push("GSAP timeline position"); }
        for (const [animation, original] of saved.animations) {
          if (original.created) continue;
          try { animation.totalTime(original.time, true).paused(original.paused); }
          catch { failures.push("GSAP animation playback"); }
        }
        for (const animation of saved.finished) {
          if (!saved.animations.get(animation)?.created) continue;
          try { animation.totalProgress(1, true).pause(); }
          catch { failures.push("GSAP entrance playback"); }
        }
      } finally {
        try { saved.gsap.globalTimeline.paused(saved.rootPaused); }
        catch { failures.push("GSAP timeline playback"); }
        // Always release the page ticker, even if one invalidated tween throws.
        try { saved.gsap.ticker.wake(); }
        catch { failures.push("GSAP ticker playback"); }
      }
    }
    for (const [node, original] of saved.media) {
      try {
        node.playbackRate = original.rate;
        if (node.readyState > 0 && Number.isFinite(original.time) && Number.isFinite(node.duration) &&
            node.currentSrc === original.source && node.srcObject === original.stream && Math.abs(node.currentTime - original.time) > .001) node.currentTime = original.time;
        const play = saved.requestedPlayback.get(node) ?? !original.paused;
        if (play) Promise.resolve(saved.play.call(node)).catch(() => {});
        else if (!node.paused) saved.pause.call(node);
      } catch (error) { failures.push(`media playback (${error.message})`); }
    }
    return { restored: !failures.length, failures };
  }
  function begin(id) {
    if (capture) restore(capture.id);
    const gsap = window.gsap?.globalTimeline?.getChildren && window.gsap?.ticker?.sleep ? window.gsap : null;
    capture = { id, gsap, initial: new Set(gsap?.globalTimeline.getChildren(true, true, true) || []),
      rootTime: gsap?.globalTimeline.totalTime(), rootPaused: gsap?.globalTimeline.paused(),
      animations: new Map(), finished: new Set(), media: new Map(), requestedPlayback: new Map(),
      play: HTMLMediaElement.prototype.play, pause: HTMLMediaElement.prototype.pause,
      touched: performance.now(), watchdog: 0 };
    // Snapshot before finishing entrance effects or pausing the page's ticker.
    for (const animation of capture.initial) capture.animations.set(animation, {
      time: animation.totalTime(), paused: animation.paused(), created: false
    });
    capture.playProxy = function () {
      if (!capture) return savedPlay.call(this);
      rememberMedia(this);
      capture.requestedPlayback.set(this, true);
      return Promise.resolve();
    };
    capture.pauseProxy = function () {
      if (!capture) return savedPause.call(this);
      rememberMedia(this, false);
      capture.requestedPlayback.set(this, false);
      return savedPause.call(this);
    };
    const savedPlay = capture.play, savedPause = capture.pause;
    if (Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "play")?.writable) HTMLMediaElement.prototype.play = capture.playProxy;
    if (Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "pause")?.writable) HTMLMediaElement.prototype.pause = capture.pauseProxy;
    capture.watchdog = setInterval(() => {
      if (capture && performance.now() - capture.touched > 9000) restore(capture.id);
    }, 1000);
    update();
    return { handled: true, gsap: Boolean(gsap), media: capture.media.size };
  }
  const receive = event => {
    const message = event.data;
    if (event.source !== window || message?.source !== "winshot-effects-request-v3" || typeof message.nonce !== "string") return;
    let result;
    try {
      if (message.action === "begin") result = begin(message.id);
      else if (message.action === "restore") result = restore(message.id);
      else if (message.action === "update" && capture?.id === message.id) { update(); result = { handled: true }; }
      else result = { handled: false };
    } catch (error) { result = { error: String(error.message) }; }
    postMessage({ source: "winshot-effects-response-v3", nonce: message.nonce, result }, "*");
  };
  addEventListener("message", receive);
  window[key] = { version, dispose() {
    try { if (capture) restore(capture.id); }
    finally { removeEventListener("message", receive); }
  } };
})();
