(() => {
  const key = Symbol.for("winshot.page-effects.v1");
  if (window[key]) return;
  window[key] = true;
  let capture = null;
  const roots = () => {
    const all = [document];
    for (let i = 0; i < all.length; i++) for (const node of all[i].querySelectorAll("*")) if (node.shadowRoot) all.push(node.shadowRoot);
    return all;
  };
  function rememberMedia(node) {
    if (!capture.media.has(node)) capture.media.set(node, {
      paused: node.paused, time: node.currentTime, rate: node.playbackRate,
      source: node.currentSrc, stream: node.srcObject
    });
    node.pause();
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
    if (HTMLMediaElement.prototype.play === saved.playProxy) HTMLMediaElement.prototype.play = saved.play;
    const failures = [];
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
        if (!original.paused || saved.requestedPlay.has(node)) Promise.resolve(saved.play.call(node)).catch(() => {});
      } catch (error) { failures.push(`media playback (${error.message})`); }
    }
    return { restored: !failures.length, failures };
  }
  function begin(id) {
    if (capture) restore(capture.id);
    const gsap = window.gsap?.globalTimeline?.getChildren && window.gsap?.ticker?.sleep ? window.gsap : null;
    capture = { id, gsap, initial: new Set(gsap?.globalTimeline.getChildren(true, true, true) || []),
      rootTime: gsap?.globalTimeline.totalTime(), rootPaused: gsap?.globalTimeline.paused(),
      animations: new Map(), finished: new Set(), media: new Map(), requestedPlay: new Set(), play: HTMLMediaElement.prototype.play,
      touched: performance.now(), watchdog: 0 };
    // Snapshot before finishing entrance effects or pausing the page's ticker.
    for (const animation of capture.initial) capture.animations.set(animation, {
      time: animation.totalTime(), paused: animation.paused(), created: false
    });
    capture.playProxy = function () {
      if (!capture) return savedPlay.call(this);
      rememberMedia(this);
      capture.requestedPlay.add(this);
      return Promise.resolve();
    };
    const savedPlay = capture.play;
    if (Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, "play")?.writable) HTMLMediaElement.prototype.play = capture.playProxy;
    capture.watchdog = setInterval(() => {
      if (capture && performance.now() - capture.touched > 9000) restore(capture.id);
    }, 1000);
    update();
    return { handled: true, gsap: Boolean(gsap), media: capture.media.size };
  }
  addEventListener("message", event => {
    const message = event.data;
    if (event.source !== window || message?.source !== "winshot-effects-request" || typeof message.nonce !== "string") return;
    let result;
    try {
      if (message.action === "begin") result = begin(message.id);
      else if (message.action === "restore") result = restore(message.id);
      else if (message.action === "update" && capture?.id === message.id) { update(); result = { handled: true }; }
      else result = { handled: false };
    } catch (error) { result = { error: String(error.message) }; }
    postMessage({ source: "winshot-effects-response", nonce: message.nonce, result }, "*");
  });
})();
