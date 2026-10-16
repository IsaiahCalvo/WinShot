(() => {
  if (globalThis.__winshotCaptureScriptInstalled) return;
  globalThis.__winshotCaptureScriptInstalled = true;

  const WATCHDOG_MS = 9000;
  const DOCUMENT_TOKEN = [...crypto.getRandomValues(new Uint32Array(4))].join("-");
  const PICKER_Z = "2147483647";
  let session = null;
  let frameHostSession = null;
  let pickerCleanup = null;
  let activePickerId = null;
  let pendingPickerState = null;
  const cancelledPickerIds = new Set();
  const announcedFrames = new Map();

  function pageEffects(action, id) {
    return new Promise(resolve => {
      const nonce = crypto.randomUUID();
      const done = result => { clearTimeout(timer); removeEventListener("message", receive); resolve(result); };
      const receive = event => {
        if (event.source === window && event.data?.source === "winshot-effects-response-v3" && event.data.nonce === nonce) done(event.data.result);
      };
      const timer = setTimeout(() => done({ error: "The page animation bridge did not respond." }), 700);
      addEventListener("message", receive);
      postMessage({ source: "winshot-effects-request-v3", action, id, nonce }, "*");
    });
  }

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const nextPaint = (onPaint = null) => new Promise((resolve) => {
    let frame;
    const finish = (painted = false) => {
      clearTimeout(timer); cancelAnimationFrame(frame);
      if (painted && session) onPaint?.();
      resolve();
    };
    // A hidden frame/tab can suspend rAF. Keep messages bounded so cancellation
    // and the watchdog can still unwind a capture when the user switches tabs.
    const timer = setTimeout(() => finish(), 250);
    frame = requestAnimationFrame(() => { frame = requestAnimationFrame(() => finish(true)); });
  });

  function scrollingElement() {
    return document.scrollingElement || document.documentElement;
  }

  function isScrollable(element) {
    if (!(element instanceof Element)) return false;
    const style = getComputedStyle(element);
    const y = /(auto|scroll|overlay|hidden)/.test(style.overflowY) && element.scrollHeight > element.clientHeight + 1;
    const x = /(auto|scroll|overlay|hidden)/.test(style.overflowX) && element.scrollWidth > element.clientWidth + 1;
    return { x, y, any: x || y };
  }

  // parentElement stops at shadow boundaries; hop to the shadow host so walks keep
  // going for nodes picked inside open shadow roots.
  function composedParent(node) {
    if (node?.parentElement) return node.parentElement;
    const root = node?.getRootNode?.();
    return root instanceof ShadowRoot ? root.host : null;
  }

  function hasTransparentAncestor(node, cache = null) {
    const visited = [];
    let transparent = false;
    for (let current = node; current instanceof Element; current = composedParent(current)) {
      if (cache?.has(current)) { transparent = cache.get(current); break; }
      if (cache) visited.push(current);
      if (Number(getComputedStyle(current).opacity) === 0) { transparent = true; break; }
    }
    for (const current of visited) cache.set(current, transparent);
    return transparent;
  }

  function floatingBounds(node, authored = false) {
    const style = getComputedStyle(node), own = node.getBoundingClientRect();
    const visible = target => authored ? session.floatingVisibility.get(target)?.visible : getComputedStyle(target).visibility === "visible";
    let bounds = visible(node) && own.width > 0 && own.height > 0 ? own : null;
    if (!bounds || style.overflowX === "visible" || style.overflowY === "visible") {
      const children = [...node.querySelectorAll("*")];
      if (node.shadowRoot) children.push(...node.shadowRoot.querySelectorAll("*"));
      for (const child of children) {
        const rect = child.getBoundingClientRect();
        if (!rect.width || !rect.height || !visible(child) || hasTransparentAncestor(child)) continue;
        if (!bounds) bounds = rect;
        else {
          const left = Math.min(bounds.left, rect.left), top = Math.min(bounds.top, rect.top);
          const right = Math.max(bounds.right, rect.right), bottom = Math.max(bounds.bottom, rect.bottom);
          bounds = { left, top, right, bottom, width: right - left, height: bottom - top };
        }
      }
    }
    return bounds;
  }

  function openRoots() {
    const roots = [document];
    for (let index = 0; index < roots.length; index++) {
      for (const element of roots[index].querySelectorAll("*")) {
        if (element.shadowRoot) roots.push(element.shadowRoot);
      }
    }
    return roots;
  }

  function nearestScrollOwner(element) {
    for (let node = composedParent(element); node && node !== document.body; node = composedParent(node)) {
      if (isScrollable(node).any) return node;
    }
    return scrollingElement();
  }

  function rememberStyle(element, property, value, priority = "important") {
    if (!session) return;
    if (element.style.getPropertyValue(property) === value && element.style.getPropertyPriority(property) === priority) return;
    let entry = session.changedStyles.get(element);
    if (!entry) {
      entry = new Map();
      session.changedStyles.set(element, entry);
    }
    if (!entry.has(property)) {
      entry.set(property, {
        value: element.style.getPropertyValue(property),
        priority: element.style.getPropertyPriority(property)
      });
    }
    element.style.setProperty(property, value, priority);
  }

  function snapshotSelection() {
    const selection = getSelection();
    if (!selection) return [];
    const ranges = [];
    for (let i = 0; i < selection.rangeCount; i++) ranges.push(selection.getRangeAt(i).cloneRange());
    return ranges;
  }

  async function restore(reason = "requested") {
    if (!session) return { restored: true, reason, failures: [] };
    const current = session;
    session = null;
    clearInterval(current.watchdog);
    current.observer?.disconnect();
    const failures = [];

    for (const snapshot of current.surfaceSnapshots.values()) snapshot.remove();

    for (const [element, properties] of [...current.changedStyles].reverse()) {
      if (!(element instanceof Element)) continue;
      for (const [property, original] of properties) {
        try {
          if (original.value) element.style.setProperty(property, original.value, original.priority);
          else element.style.removeProperty(property);
        } catch {
          failures.push(`style:${property}`);
        }
      }
    }
    for (const style of current.freezeStyles) {
      style.remove();
      if (style.isConnected) failures.push("freeze-style");
    }

    for (const animation of current.captureAnimations) animation.cancel();
    for (const [animation, original] of current.changedAnimations) {
      try {
        // These effects were created by scroll reveals during capture. Keeping
        // their completed frame avoids replaying the entrance after restoration.
        if (original.created && animation.playState === "finished") continue;
        animation.playbackRate = original.playbackRate;
        animation.currentTime = original.currentTime;
        let resume = original.playState === "running";
        if (original.created && animation instanceof CSSAnimation) {
          const style = getComputedStyle(animation.effect.target);
          const index = style.animationName.split(",").map(name => name.trim()).indexOf(animation.animationName);
          const states = style.animationPlayState.split(",").map(state => state.trim());
          resume = states[Math.max(0, index) % states.length] === "running";
        }
        if (resume) animation.play();
        else if (original.playState === "paused") animation.pause();
      } catch { failures.push("animation"); }
    }

    if (current.owner instanceof Element && current.owner !== scrollingElement()) {
      current.owner.scrollTo({ ...current.ownerScroll, behavior: "instant" });
      if (Math.abs(current.owner.scrollTop - current.ownerScroll.top) > 2 || Math.abs(current.owner.scrollLeft - current.ownerScroll.left) > 2) failures.push("owner-scroll");
    }
    // The original CSS may enable smooth scrolling again. Restoration must finish
    // before acknowledging it, rather than leave the page animating toward home.
    window.scrollTo({ ...current.windowScroll, behavior: "instant" });
    // Read back — restoration is only "verified" if the page actually ended up there.
    if (Math.abs(window.scrollY - current.windowScroll.top) > 2 || Math.abs(window.scrollX - current.windowScroll.left) > 2) {
      failures.push("window-scroll");
    }

    try {
      const selection = getSelection();
      selection?.removeAllRanges();
      for (const range of current.selection) selection?.addRange(range);
      current.activeElement?.focus?.({ preventScroll: true });
      if (current.activeElement instanceof Element && current.activeElement.isConnected && document.activeElement !== current.activeElement) {
        failures.push("focus");
      }
    } catch {
      failures.push("selection");
    }

    if (current.targetElement && current.targetToken) {
      if (current.targetAttributeBefore === null) current.targetElement.removeAttribute("data-winshot-target");
      else current.targetElement.setAttribute("data-winshot-target", current.targetAttributeBefore);
    }
    const effects = await pageEffects("restore", current.id);
    if (effects.error) failures.push(`page-animation-state: ${effects.error}`);
    for (const failure of effects.failures || []) failures.push(`page-animation-state: ${failure}`);
    return { restored: failures.length === 0, reason, failures };
  }

  function savePickerState() {
    clearTimeout(pendingPickerState?.timer);
    const state = {
      windowScroll: { left: window.scrollX, top: window.scrollY },
      activeElement: document.activeElement,
      selection: snapshotSelection(),
      timer: 0
    };
    // Leaked-state watchdog only: a user can legitimately hover or hold a drag far
    // longer than WATCHDOG_MS, so the timer re-arms while a picker overlay is open
    // and fires only once the overlay is gone and nothing consumed the state.
    const tick = () => {
      if (pickerCleanup) {
        state.timer = setTimeout(tick, WATCHDOG_MS);
        return;
      }
      restorePickerState();
    };
    state.timer = setTimeout(tick, WATCHDOG_MS);
    pendingPickerState = state;
  }

  function restorePickerState() {
    if (!pendingPickerState) return;
    const state = pendingPickerState;
    pendingPickerState = null;
    clearTimeout(state.timer);
    window.scrollTo(state.windowScroll.left, state.windowScroll.top);
    try {
      const selection = getSelection();
      selection?.removeAllRanges();
      for (const range of state.selection) selection?.addRange(range);
      state.activeElement?.focus?.({ preventScroll: true });
    } catch {
      // The page may have removed the focused node while the picker was open.
    }
  }

  function documentSize() {
    const root = scrollingElement();
    const body = document.body;
    return {
      width: Math.max(root.scrollWidth, root.clientWidth, body?.scrollWidth || 0, body?.offsetWidth || 0),
      height: Math.max(root.scrollHeight, root.clientHeight, body?.scrollHeight || 0, body?.offsetHeight || 0)
    };
  }

  function viewport() {
    const visual = window.visualViewport;
    return {
      left: visual?.pageLeft ?? window.scrollX,
      top: visual?.pageTop ?? window.scrollY,
      offsetLeft: visual?.offsetLeft ?? 0,
      offsetTop: visual?.offsetTop ?? 0,
      width: visual?.width ?? document.documentElement.clientWidth,
      height: visual?.height ?? document.documentElement.clientHeight,
      rasterWidth: innerWidth / (visual?.scale || 1),
      rasterHeight: innerHeight / (visual?.scale || 1),
      scale: visual?.scale ?? 1,
      devicePixelRatio: window.devicePixelRatio
    };
  }

  function frameContext() {
    if (window === top) return { isTop: true, accessible: true, left: 0, top: 0, topViewport: viewport() };
    let current = window;
    let left = 0;
    let topOffset = 0;
    try {
      while (current !== current.top) {
        const frame = current.frameElement;
        if (!frame) throw new Error("Frame element is unavailable.");
        const rect = frame.getBoundingClientRect();
        left += rect.left + frame.clientLeft;
        topOffset += rect.top + frame.clientTop;
        current = current.parent;
      }
      const visual = current.visualViewport;
      return {
        isTop: false,
        accessible: true,
        left,
        top: topOffset,
        topViewport: {
          left: visual?.pageLeft ?? current.scrollX,
          top: visual?.pageTop ?? current.scrollY,
          offsetLeft: visual?.offsetLeft ?? 0,
          offsetTop: visual?.offsetTop ?? 0,
          width: visual?.width ?? current.document.documentElement.clientWidth,
          height: visual?.height ?? current.document.documentElement.clientHeight,
          rasterWidth: current.innerWidth / (visual?.scale || 1),
          rasterHeight: current.innerHeight / (visual?.scale || 1)
        }
      };
    } catch {
      return { isTop: false, accessible: false, url: location.href };
    }
  }

  function restoreFrameHost() {
    if (!frameHostSession) return { restored: true };
    const saved = frameHostSession;
    frameHostSession = null;
    clearInterval(saved.watchdog);
    if (saved.frame) {
      if (saved.styleBefore === null) saved.frame.removeAttribute("style");
      else saved.frame.setAttribute("style", saved.styleBefore);
    }
    window.scrollTo(saved.scroll.left, saved.scroll.top);
    try { saved.activeElement?.focus?.({ preventScroll: true }); } catch { /* The page may have replaced it. */ }
    return { restored: true };
  }

  // querySelectorAll never pierces shadow roots — walk them so frames hosted inside
  // web components are still matched and counted.
  function allFrameElements(root = document) {
    const frames = [...root.querySelectorAll("iframe,frame")];
    for (const element of root.querySelectorAll("*")) {
      if (element.shadowRoot) frames.push(...allFrameElements(element.shadowRoot));
    }
    return frames;
  }

  addEventListener("message", (event) => {
    const data = event.data;
    if (!data || data.source !== "winshot-frame" || typeof data.token !== "string") return;
    const frame = allFrameElements().find((candidate) => candidate.contentWindow === event.source);
    if (!frame) return;
    announcedFrames.set(data.token, frame);
    setTimeout(() => announcedFrames.delete(data.token), 5000);
  });

  async function locateFrameHost(request) {
    restoreFrameHost();
    let frame = null;
    const deadline = performance.now() + 1500;
    while (!frame && performance.now() < deadline) {
      frame = announcedFrames.get(request.frameToken) || null;
      if (!frame) await sleep(25);
    }
    announcedFrames.delete(request.frameToken);
    if (!frame) throw new Error("The selected frame could not be matched to its exact browser frame identity.");
    frameHostSession = {
      id: request.sessionId,
      scroll: { left: window.scrollX, top: window.scrollY },
      activeElement: document.activeElement,
      frame,
      styleBefore: frame.getAttribute("style"),
      lastHeartbeat: Date.now(),
      watchdog: 0
    };
    frameHostSession.watchdog = setInterval(() => {
      if (frameHostSession && Date.now() - frameHostSession.lastHeartbeat > WATCHDOG_MS) restoreFrameHost();
    }, 1000);
    let view = viewport();
    if (frame.clientWidth > view.width - 8) frame.style.setProperty("width", `${Math.max(1, Math.floor(view.width - 8))}px`, "important");
    if (frame.clientHeight > view.height - 8) frame.style.setProperty("height", `${Math.max(1, Math.floor(view.height - 8))}px`, "important");
    frame.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
    await nextPaint();
    const rect = frame.getBoundingClientRect();
    view = viewport();
    const left = rect.left + frame.clientLeft;
    const topOffset = rect.top + frame.clientTop;
    if (left < 0 || topOffset < 0 || left + frame.clientWidth > view.width || topOffset + frame.clientHeight > view.height) {
      restoreFrameHost();
      throw new Error("The frame viewport could not be fitted inside the visible browser area for safe capture.");
    }
    return { located: true, left, top: topOffset, width: frame.clientWidth, height: frame.clientHeight, topViewport: view };
  }

  function ownerClientRect(owner) {
    if (owner === scrollingElement()) {
      const view = viewport();
      return { left: 0, top: 0, width: view.width, height: view.height };
    }
    const rect = owner.getBoundingClientRect();
    const scale = ownerScale(owner);
    return {
      left: rect.left + owner.clientLeft * scale.x,
      top: rect.top + owner.clientTop * scale.y,
      width: owner.clientWidth * scale.x,
      height: owner.clientHeight * scale.y
    };
  }

  function ownerScale(owner) {
    const rect = owner.getBoundingClientRect();
    return {
      x: owner.offsetWidth ? rect.width / owner.offsetWidth : 1,
      y: owner.offsetHeight ? rect.height / owner.offsetHeight : 1
    };
  }

  function visibleClientRect(element) {
    let result = ownerClientRect(element);
    for (let ancestor = composedParent(element); ancestor && ancestor !== document.body; ancestor = composedParent(ancestor)) {
      if (!isScrollable(ancestor).any) continue;
      const rect = ownerClientRect(ancestor);
      const left = Math.max(result.left, rect.left);
      const top = Math.max(result.top, rect.top);
      const right = Math.min(result.left + result.width, rect.left + rect.width);
      const bottom = Math.min(result.top + result.height, rect.top + rect.height);
      result = { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
    }
    return result;
  }

  function currentMetrics() {
    if (!session) throw new Error("No active WinShot capture session.");
    const view = viewport();
    const owner = session.owner;
    let width;
    let height;
    let clip;
    let destX;
    let destY;
    let reachedX;
    let reachedY;

    if (session.fixedTarget) {
      // The element ignores window scroll — clip its live viewport rect where it sits.
      const rect = session.targetElement.getBoundingClientRect();
      width = rect.width;
      height = rect.height;
      const left = Math.max(rect.left, 0);
      const top = Math.max(rect.top, 0);
      const right = Math.min(rect.right, view.width);
      const bottom = Math.min(rect.bottom, view.height);
      clip = {
        left: left + view.offsetLeft,
        top: top + view.offsetTop,
        width: Math.max(0, right - left),
        height: Math.max(0, bottom - top)
      };
      destX = Math.max(0, left - rect.left);
      destY = Math.max(0, top - rect.top);
      reachedX = window.scrollX;
      reachedY = window.scrollY;
    } else if (session.kind === "document" || session.kind === "region" || session.kind === "page-element") {
      const target = session.kind === "document"
        ? { left: 0, top: 0, ...documentSize() }
        : session.targetRect;
      width = target.width;
      height = target.height;
      const left = Math.max(target.left, view.left);
      const top = Math.max(target.top, view.top);
      const right = Math.min(target.left + target.width, view.left + view.width);
      const bottom = Math.min(target.top + target.height, view.top + view.height);
      clip = {
        left: left - view.left + view.offsetLeft,
        top: top - view.top + view.offsetTop,
        width: Math.max(0, right - left),
        height: Math.max(0, bottom - top)
      };
      destX = Math.max(0, left - target.left);
      destY = Math.max(0, top - target.top);
      reachedX = window.scrollX;
      reachedY = window.scrollY;
    } else if (session.kind === "self-scroller") {
      const rect = visibleClientRect(owner);
      const scale = ownerScale(owner);
      width = owner.scrollWidth * scale.x;
      height = owner.scrollHeight * scale.y;
      const left = Math.max(0, rect.left);
      const top = Math.max(0, rect.top);
      const right = Math.min(view.width, rect.left + rect.width);
      const bottom = Math.min(view.height, rect.top + rect.height);
      clip = { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
      const fullRect = ownerClientRect(owner);
      destX = owner.scrollLeft * scale.x + left - fullRect.left;
      destY = owner.scrollTop * scale.y + top - fullRect.top;
      reachedX = owner.scrollLeft;
      reachedY = owner.scrollTop;
    } else {
      const elementRect = session.targetElement.getBoundingClientRect();
      const ownerRect = ownerClientRect(owner);
      width = session.targetRect.width;
      height = session.targetRect.height;
      const left = Math.max(elementRect.left, ownerRect.left, 0);
      const top = Math.max(elementRect.top, ownerRect.top, 0);
      const right = Math.min(elementRect.right, ownerRect.left + ownerRect.width, view.width);
      const bottom = Math.min(elementRect.bottom, ownerRect.top + ownerRect.height, view.height);
      clip = { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
      destX = Math.max(0, left - elementRect.left);
      destY = Math.max(0, top - elementRect.top);
      reachedX = owner.scrollLeft;
      reachedY = owner.scrollTop;
    }

    return {
      kind: session.kind,
      width,
      height,
      viewport: view,
      clip,
      destX,
      destY,
      reachedX,
      reachedY,
      mutationCount: session.mutationCount,
      floatingRects: session.floating.flatMap(({ node }) => {
        const style = getComputedStyle(node);
        if (style.position !== "fixed" || hasTransparentAncestor(node)) return [];
        const rect = floatingBounds(node);
        if (!rect) return [];
        const left = Math.max(rect.left, clip.left), top = Math.max(rect.top, clip.top);
        const right = Math.min(rect.right, clip.left + clip.width), bottom = Math.min(rect.bottom, clip.top + clip.height);
        return right > left && bottom > top ? [{ left: left - clip.left, top: top - clip.top, width: right - left, height: bottom - top }] : [];
      }),
      documentToken: DOCUMENT_TOKEN,
      url: location.href,
      frame: frameContext()
    };
  }

  async function waitForStable(timeoutMs) {
    const start = performance.now();
    let previous = "";
    let repeats = 0;
    while (performance.now() - start < timeoutMs) {
      await nextPaint(snapshotVisibleCanvases);
      await pageEffects("update", session.id);
      refreshCaptureEffects();
      const metrics = currentMetrics();
      const pendingImages = visibleImages(metrics).some((image) => !image.complete);
      const signature = JSON.stringify([
        Math.round(metrics.width * 10), Math.round(metrics.height * 10),
        Math.round(metrics.reachedX * 10), Math.round(metrics.reachedY * 10),
        Math.round(metrics.clip.width * 10), Math.round(metrics.clip.height * 10),
        metrics.mutationCount
      ]);
      if (signature === previous) repeats++;
      else repeats = 0;
      if (repeats >= 1 && !pendingImages && !session.animationIssues.size) {
        // Image dimensions can be stable while their pixels are still loading.
        // Decode only images in this tile; offscreen lazy images must not block it.
        const remaining = Math.max(0, timeoutMs - (performance.now() - start));
        const decoded = await Promise.race([
          Promise.allSettled(visibleImages(metrics).map((image) => image.decode())).then(() => true),
          sleep(remaining).then(() => false)
        ]);
        if (decoded) return { stable: true, metrics: currentMetrics() };
      }
      previous = signature;
      await sleep(60);
    }
    return { stable: false, metrics: currentMetrics(), warnings: [...session.animationIssues] };
  }

  function pageAnimations() {
    return [...new Set(session.roots.flatMap(root => root === document
      ? document.getAnimations()
      : [...root.querySelectorAll("*")].flatMap(node => node.getAnimations())))];
  }

  function snapshotVisibleCanvases() {
    // Copy browser-rendered canvas pixels during the paint callback's microtask,
    // before WebGL clears a non-preserved drawing buffer. Keep the original
    // canvas/context connected and running; cover it with a static canvas in the
    // same containing block. No page CSP exception or image serialization needed.
    for (const root of session.roots) for (const canvas of root.querySelectorAll("canvas:not([data-winshot-surface])")) {
      if (session.surfaceSnapshots.has(canvas) || !canvas.width || !canvas.height) continue;
      const rect = canvas.getBoundingClientRect(), style = getComputedStyle(canvas);
      if (!rect.width || !rect.height || rect.bottom <= 0 || rect.right <= 0 || rect.top >= innerHeight || rect.left >= innerWidth ||
          style.visibility !== "visible" || hasTransparentAncestor(canvas)) continue;
      const pixels = canvas.width * canvas.height;
      if (pixels + session.surfacePixels > 32_000_000) continue;
      const snapshot = document.createElement("canvas");
      snapshot.width = canvas.width; snapshot.height = canvas.height;
      snapshot.dataset.winshotSurface = session.id;
      snapshot.setAttribute("aria-hidden", "true");
      for (const property of style) snapshot.style.setProperty(property, style.getPropertyValue(property), "important");
      snapshot.style.setProperty("pointer-events", "none", "important");
      if (!["fixed", "absolute"].includes(style.position)) {
        snapshot.style.setProperty("position", "absolute", "important");
        snapshot.style.setProperty("left", `${canvas.offsetLeft - (parseFloat(style.marginLeft) || 0)}px`, "important");
        snapshot.style.setProperty("top", `${canvas.offsetTop - (parseFloat(style.marginTop) || 0)}px`, "important");
        snapshot.style.setProperty("right", "auto", "important");
        snapshot.style.setProperty("bottom", "auto", "important");
      }
      try {
        const context = snapshot.getContext("2d", { willReadFrequently: true });
        context.drawImage(canvas, 0, 0);
        try {
          // An idle WebGL buffer may already have been cleared by the browser.
          // Never cover its still-visible front buffer with a transparent copy.
          const rgba = context.getImageData(0, 0, snapshot.width, snapshot.height).data;
          let painted = false;
          for (let i = 3; i < rgba.length; i += 4) if (rgba[i]) { painted = true; break; }
          if (!painted) continue;
        } catch (error) {
          // A tainted 2D canvas can be copied for display, though script cannot
          // inspect its pixels. Chrome's actual screenshot still owns export.
          if (error.name !== "SecurityError") throw error;
        }
        canvas.after(snapshot);
        const placed = snapshot.getBoundingClientRect();
        if (["left", "top", "width", "height"].some(key => Math.abs(placed[key] - rect[key]) > 1)) {
          snapshot.remove(); continue;
        }
        // An absolutely positioned copy can escape a non-positioned overflow
        // ancestor's clip. Do not cover those shapes until placement is provable.
        let clipped = false;
        for (let parent = composedParent(canvas); parent && parent !== canvas.offsetParent; parent = composedParent(parent)) {
          const parentStyle = getComputedStyle(parent), bounds = parent.getBoundingClientRect();
          if ((/(hidden|clip|scroll|auto)/.test(parentStyle.overflowX) && (rect.left < bounds.left - 1 || rect.right > bounds.right + 1)) ||
              (/(hidden|clip|scroll|auto)/.test(parentStyle.overflowY) && (rect.top < bounds.top - 1 || rect.bottom > bounds.bottom + 1))) clipped = true;
        }
        if (clipped) { snapshot.remove(); continue; }
        session.surfaceSnapshots.set(canvas, snapshot);
        session.surfacePixels += pixels;
        rememberStyle(canvas, "visibility", "hidden", "important");
      } catch { snapshot.remove(); }
    }
  }

  function rememberAnimation(animation, created) {
    if (!session.changedAnimations.has(animation)) session.changedAnimations.set(animation, {
      currentTime: animation.currentTime, playState: animation.playState,
      playbackRate: animation.playbackRate, created
    });
    return session.changedAnimations.get(animation);
  }

  function refreshCaptureEffects() {
    session.animationIssues.clear();
    refreshOpenRoots();
    const nodes = [...session.pendingNodes];
    session.pendingNodes.clear();
    trackFloatingNodes(nodes);
    setFloatingVisibility(session.atStart, session.atEnd);
    for (const animation of pageAnimations()) {
      if (session.captureAnimations.has(animation) || animation.playState === "finished" || animation.playState === "idle") continue;
      const original = rememberAnimation(animation, true);
      // Respect animations that the page/user had already paused before capture.
      if (!original.created && original.playState === "paused") continue;
      try {
        if (animation.timeline && !(animation.timeline instanceof DocumentTimeline)) {
          // A progress-based scroll/view timeline cannot be finished(). Pin its
          // final keyframe with a temporary document-timeline effect instead.
          if (!session.pinnedAnimations.has(animation)) {
            const frame = { ...animation.effect.getKeyframes().at(-1) };
            delete frame.offset; delete frame.computedOffset; delete frame.easing; delete frame.composite;
            const pin = animation.effect.target.animate([frame, frame], { duration: 1, fill: "both", pseudoElement: animation.effect.pseudoElement });
            pin.pause(); pin.currentTime = 1;
            session.captureAnimations.add(pin);
            session.pinnedAnimations.add(animation);
          }
        } else if (Number.isFinite(animation.effect?.getComputedTiming().endTime)) {
          // Pausing an entrance animation at opacity:0 silently loses content.
          // Complete finite effects; pause repeating effects at their current frame.
          animation.finish();
        } else animation.pause();
      } catch { session.animationIssues.add("An animation could not be stabilized."); }
    }
  }

  function refreshOpenRoots() {
    for (const root of openRoots()) {
      if (session.roots.includes(root)) continue;
      session.roots.push(root);
      for (const animation of pageAnimations()) rememberAnimation(animation, true);
      installFreezeStyle(root, session.id);
      session.observer?.observe(root, { childList: true, subtree: true, attributes: true });
      for (const node of root.querySelectorAll("*")) session.pendingNodes.add(node);
    }
  }

  function visibleImages(metrics) {
    const { left, top, width, height } = metrics.clip;
    return session.roots.flatMap((root) => [...root.querySelectorAll("img[src], img[srcset]")]).filter((image) => {
      const rect = image.getBoundingClientRect();
      return rect.width > 0 && rect.height > 0 && rect.right > left && rect.bottom > top &&
        rect.left < left + width && rect.top < top + height && getComputedStyle(image).visibility !== "hidden" && !hasTransparentAncestor(image);
    });
  }

  function semanticSnapshot(metrics, limit = 5000) {
    const clip = metrics.clip;
    const right = clip.left + clip.width;
    const bottom = clip.top + clip.height;
    const text = [];
    const links = [];
    const seenText = new Set();
    const seenLinks = new Set();
    // This function runs synchronously: authored opacity cannot change between
    // these reads. Share ancestor results across text/links in this tile only.
    const transparentAncestors = new Map();

    const intersect = (rect) => {
      const left = Math.max(rect.left, clip.left);
      const top = Math.max(rect.top, clip.top);
      const clippedRight = Math.min(rect.right, right);
      const clippedBottom = Math.min(rect.bottom, bottom);
      if (clippedRight - left < 0.5 || clippedBottom - top < 0.5) return null;
      return {
        x: metrics.destX + left - clip.left,
        y: metrics.destY + top - clip.top,
        width: clippedRight - left,
        height: clippedBottom - top
      };
    };

    const roots = session?.roots || openRoots();

    for (const root of roots) {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      while (text.length < limit) {
        const node = walker.nextNode();
        if (!node) break;
        const value = node.nodeValue?.replace(/\s+/g, " ").trim();
        if (!value || !(node.parentElement instanceof Element)) continue;
        const style = getComputedStyle(node.parentElement);
        if (style.display === "none" || style.visibility === "hidden" || hasTransparentAncestor(node.parentElement, transparentAncestors)) continue;
        const range = document.createRange();
        range.selectNodeContents(node);
        for (const rect of range.getClientRects()) {
          const mapped = intersect(rect);
          if (!mapped) continue;
          const key = `${value}|${mapped.x.toFixed(1)}|${mapped.y.toFixed(1)}`;
          if (seenText.has(key)) continue;
          seenText.add(key);
          text.push({ ...mapped, text: value.slice(0, 1000), fontSize: Math.max(6, parseFloat(style.fontSize) || 12) });
          if (text.length >= limit) break;
        }
      }

      for (const anchor of root.querySelectorAll("a[href]")) {
        if (links.length >= 2000) break;
        const style = getComputedStyle(anchor);
        if (style.display === "none" || style.visibility === "hidden" || hasTransparentAncestor(anchor, transparentAncestors)) continue;
        for (const rect of anchor.getClientRects()) {
          const mapped = intersect(rect);
          if (!mapped) continue;
          const url = anchor.href;
          if (!/^https?:/i.test(url)) continue;
          const key = `${url}|${mapped.x.toFixed(1)}|${mapped.y.toFixed(1)}`;
          if (seenLinks.has(key)) continue;
          seenLinks.add(key);
          links.push({ ...mapped, url });
        }
      }
    }
    return { text, links };
  }

  function visibleSemantics() {
    const view = viewport();
    const metrics = {
      clip: { left: view.offsetLeft, top: view.offsetTop, width: view.width, height: view.height },
      destX: 0,
      destY: 0
    };
    return { metrics: { width: view.rasterWidth, height: view.rasterHeight }, semantics: semanticSnapshot(metrics) };
  }

  function collectWarnings() {
    const warnings = [];
    let inaccessibleFrames = 0;
    for (const frame of allFrameElements()) {
      try {
        if (!frame.contentDocument) inaccessibleFrames++;
      } catch {
        inaccessibleFrames++;
      }
    }
    if (inaccessibleFrames) {
      warnings.push(`${inaccessibleFrames} cross-origin frame(s) are visible in this page capture; use "Capture a scrolling frame" for their hidden content when browser access permits.`);
    }
    if (document.querySelector("video")) {
      warnings.push("Video is captured at the displayed frame. DRM-protected video may be blank by browser policy.");
    }
    if (document.querySelector("canvas")) {
      warnings.push("Canvas/WebGL is captured as browser-rendered pixels; rapidly changing frames may reduce seam confidence.");
    }
    return warnings;
  }

  // Pickers mark nodes found via composedPath(), which pierces open shadow roots —
  // the lookup must pierce them too.
  function findMarkedTarget(token) {
    const selector = `[data-winshot-target="${CSS.escape(token)}"]`;
    const roots = [document];
    for (let index = 0; index < roots.length; index++) {
      const found = roots[index].querySelector(selector);
      if (found) return found;
      for (const element of roots[index].querySelectorAll("*")) {
        if (element.shadowRoot && !roots.includes(element.shadowRoot)) roots.push(element.shadowRoot);
      }
    }
    return null;
  }

  function mainScrollOwner() {
    const view = viewport();
    const size = documentSize();
    if (size.height > view.height + 2 || size.width > view.width + 2) return null;
    const candidates = new Set();
    for (const [x, y] of [[0.5, 0.5], [0.65, 0.5], [0.5, 0.75]]) {
      let element = document.elementFromPoint(view.width * x, view.height * y);
      while (element?.shadowRoot) {
        const child = element.shadowRoot.elementFromPoint(view.width * x, view.height * y);
        if (!child || child === element) break;
        element = child;
      }
      for (let node = element; node && node !== document.body; node = composedParent(node)) {
        if (node === scrollingElement() || !isScrollable(node).y) continue;
        const rect = visibleClientRect(node);
        if (rect.width >= view.width * 0.4 && rect.height >= view.height * 0.55 &&
            rect.width * rect.height >= view.width * view.height * 0.3) candidates.add(node);
      }
    }
    return [...candidates].sort((a, b) => b.clientWidth * b.clientHeight - a.clientWidth * a.clientHeight)[0] || null;
  }

  function classifyTarget(request) {
    if (request.mode === "full-page") {
      const owner = mainScrollOwner();
      if (owner) return {
        kind: "self-scroller", owner, targetElement: owner, automaticScroller: true,
        targetRect: { left: 0, top: 0, width: owner.scrollWidth, height: owner.scrollHeight }
      };
      return { kind: "document", owner: scrollingElement(), targetRect: { left: 0, top: 0, ...documentSize() } };
    }
    if (request.mode === "region") {
      return { kind: "region", owner: scrollingElement(), targetRect: request.target.rect };
    }
    if (request.mode === "element") {
      const element = findMarkedTarget(request.target.token);
      if (!element) throw new Error("The selected element is no longer on the page.");
      const ownScroll = isScrollable(element);
      const rect = element.getBoundingClientRect();
      if (ownScroll.any) {
        return {
          kind: "self-scroller",
          owner: element,
          targetElement: element,
          targetRect: { left: 0, top: 0, width: element.scrollWidth, height: element.scrollHeight }
        };
      }
      // A position:fixed element (or one inside a fixed container) never moves with
      // window scroll — scrolling to its document coordinates would capture whatever
      // page content sits underneath. Capture its viewport rect in place instead.
      for (let node = element; node instanceof Element; node = composedParent(node)) {
        if (getComputedStyle(node).position === "fixed") {
          return {
            kind: "page-element",
            fixedTarget: true,
            owner: scrollingElement(),
            targetElement: element,
            targetRect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height }
          };
        }
      }
      const owner = nearestScrollOwner(element);
      if (owner === scrollingElement()) {
        return {
          kind: "page-element",
          owner,
          targetElement: element,
          targetRect: {
            left: rect.left + window.scrollX,
            top: rect.top + window.scrollY,
            width: rect.width,
            height: rect.height
          }
        };
      }
      const ownerRect = ownerClientRect(owner);
      const scale = ownerScale(owner);
      return {
        kind: "ancestor-scroller",
        owner,
        targetElement: element,
        ownerOffset: {
          left: owner.scrollLeft + (rect.left - ownerRect.left) / scale.x,
          top: owner.scrollTop + (rect.top - ownerRect.top) / scale.y
        },
        targetRect: { left: 0, top: 0, width: rect.width, height: rect.height }
      };
    }
    throw new Error(`Unsupported capture mode: ${request.mode}`);
  }

  function installFreezeStyle(root, sessionId) {
      const freezeStyle = document.createElement("style");
      freezeStyle.dataset.winshotCapture = sessionId;
      freezeStyle.textContent = `
      html { scroll-behavior: auto !important; scroll-snap-type: none !important; overflow-anchor: none !important; }
      *, *::before, *::after {
        animation-play-state: paused !important;
        transition: none !important;
        scroll-behavior: auto !important;
        scroll-snap-type: none !important;
        overflow-anchor: none !important;
        caret-color: transparent !important;
      }
    `;
      (root === document ? document.head || document.documentElement : root).append(freezeStyle);
      session.freezeStyles.push(freezeStyle);
  }

  function freezePage(classification, request) {
    for (const root of session.roots) installFreezeStyle(root, request.sessionId);

    if (classification.owner instanceof Element) {
      rememberStyle(classification.owner, "scroll-behavior", "auto");
      rememberStyle(classification.owner, "scroll-snap-type", "none");
      rememberStyle(classification.owner, "overflow-anchor", "none");
    }

    const nodes = session.roots.flatMap((root) => [...root.querySelectorAll(root === document ? "body *" : "*")]);
    trackFloatingNodes(nodes);
  }

  function rememberFloatingTree(node) {
    const nodes = [node];
    for (let i = 0; i < nodes.length && session.floatingVisibility.size < 25000; i++) {
      const current = nodes[i];
      nodes.push(...current.children);
      if (current.shadowRoot) nodes.push(...current.shadowRoot.children);
      if (session.floatingVisibility.has(current)) continue;
      // New descendants can arrive while their ancestor is capture-hidden.
      // Read their page-authored visibility with our ancestor overrides removed.
      const reverted = [];
      for (let ancestor = current; ancestor; ancestor = composedParent(ancestor)) {
        const original = session.changedStyles.get(ancestor)?.get("visibility");
        if (!original) continue;
        reverted.push({ node: ancestor, value: ancestor.style.getPropertyValue("visibility"), priority: ancestor.style.getPropertyPriority("visibility") });
        if (original.value) ancestor.style.setProperty("visibility", original.value, original.priority);
        else ancestor.style.removeProperty("visibility");
      }
      const visible = getComputedStyle(current).visibility === "visible";
      for (const entry of reverted.reverse()) {
        if (entry.value) entry.node.style.setProperty("visibility", entry.value, entry.priority);
        else entry.node.style.removeProperty("visibility");
      }
      session.floatingVisibility.set(current, { visible });
    }
  }

  function trackFloatingNodes(nodes) {
    const cap = Math.min(nodes.length, 25000);
    for (let i = 0; i < cap; i++) {
      const node = nodes[i];
      const position = getComputedStyle(node).position;
      if (position !== "fixed" && position !== "sticky") continue;
      // A fixed capture TARGET (or its fixed container) must stay visible on every tile.
      if (!node.isConnected) continue;
      if (session.fixedTarget && (node === session.targetElement || node.contains(session.targetElement))) continue;
      if (position === "fixed") rememberFloatingTree(node);
      const rect = floatingBounds(node, position === "fixed");
      if (!rect) continue;
      if (position === "sticky") {
        // Hiding a sticky element leaves a blank hole at its in-flow slot. Demoting it
        // to static paints it exactly once, where the layout puts it, in whichever tile
        // covers that scroll range. (Technique validated against kidandcat/pic + OpenScreenShot.)
        rememberStyle(node, "position", "static");
        continue;
      }
      const tall = rect.height > innerHeight * 0.5;
      const anchor = tall ? "overlay" : rect.top + rect.height / 2 < innerHeight / 2 ? "top" : "bottom";
      if (!session.floatingNodes.has(node)) {
        session.floating.push({ node, anchor, opacity: {
          value: node.style.getPropertyValue("opacity"), priority: node.style.getPropertyPriority("opacity")
        }, visibility: {
          value: node.style.getPropertyValue("visibility"), priority: node.style.getPropertyPriority("visibility")
        } });
        session.floatingNodes.add(node);
      }
    }
  }

  async function prepare(request) {
    // A stale picker overlay must never be captured into the pixels or keep
    // swallowing page events after the capture.
    pickerCleanup?.();
    await restore("superseded");
    const classification = classifyTarget(request);
    const owner = classification.owner;
    const pickerState = pendingPickerState;
    pendingPickerState = null;
    clearTimeout(pickerState?.timer);
    session = {
      id: request.sessionId,
      kind: classification.kind,
      fixedTarget: classification.fixedTarget || false,
      owner,
      ownerOffset: classification.ownerOffset || null,
      targetRect: classification.targetRect,
      targetElement: classification.targetElement || null,
      targetToken: request.target?.token || null,
      targetAttributeBefore: request.target?.attributeBefore ?? null,
      windowScroll: pickerState?.windowScroll || { left: window.scrollX, top: window.scrollY },
      ownerScroll: { left: owner.scrollLeft || 0, top: owner.scrollTop || 0 },
      activeElement: pickerState?.activeElement || document.activeElement,
      selection: pickerState?.selection || snapshotSelection(),
      changedStyles: new Map(),
      freezeStyles: [],
      roots: openRoots(),
      floating: [],
      floatingNodes: new Set(),
      floatingVisibility: new Map(),
      pendingNodes: new Set(),
      changedAnimations: new Map(),
      captureAnimations: new Set(),
      pinnedAnimations: new Set(),
      animationIssues: new Set(),
      surfaceSnapshots: new Map(),
      surfacePixels: 0,
      atStart: true,
      atEnd: false,
      mutationCount: 0,
      lastHeartbeat: Date.now(),
      watchdog: 0,
      observer: null
    };

    for (const animation of pageAnimations()) rememberAnimation(animation, false);
    const effects = await pageEffects("begin", session.id);
    if (effects.error) session.animationIssues.add(effects.error);
    freezePage(classification, request);
    session.observer = new MutationObserver(mutations => {
      if (!session) return;
      session.mutationCount++;
      const changedRoots = new Set();
      for (const mutation of mutations) {
        const candidates = mutation.type === "childList" ? [...mutation.addedNodes] : [mutation.target];
        for (const node of candidates) {
          if (!(node instanceof Element)) continue;
          changedRoots.add(node);
        }
      }
      // Animation libraries can write a wrapper's style hundreds of times in one
      // task. Expand each affected subtree once, rather than walking thousands of
      // descendants for every mutation before the pending-node Set deduplicates
      // them. Stay within parentElement boundaries: a host's querySelectorAll does
      // not visit its shadow tree, so that tree still needs its own expansion.
      for (const node of changedRoots) {
        let covered = false;
        for (let parent = node.parentElement; parent; parent = parent.parentElement) {
          if (changedRoots.has(parent)) { covered = true; break; }
        }
        if (covered) continue;
        session.pendingNodes.add(node);
        for (const child of node.querySelectorAll("*")) session.pendingNodes.add(child);
      }
    });
    for (const root of session.roots) {
      session.observer.observe(root === document ? document.documentElement : root, { childList: true, subtree: true, attributes: true });
    }
    session.watchdog = setInterval(() => {
      if (session && Date.now() - session.lastHeartbeat > WATCHDOG_MS) restore("watchdog");
    }, 1000);

    await nextPaint();
    const metrics = currentMetrics();
    const warnings = collectWarnings();
    if (classification.automaticScroller) warnings.push('Captured the main scrolling area. Use "Select an element" to capture another panel.');
    if (classification.kind === "ancestor-scroller") {
      warnings.push("The selected element is inside a separate scrolling panel; WinShot will scroll that panel and validate coverage.");
    }
    return { metrics, warnings };
  }

  function setFloatingVisibility(atStart, atEnd) {
    if (!session) return;
    session.atStart = atStart;
    session.atEnd = atEnd;
    const showing = new Map();
    for (const { node, anchor } of session.floating) {
      if (getComputedStyle(node).position !== "fixed") continue;
      showing.set(node, (anchor === "top" && atStart) || (anchor === "bottom" && atEnd) || (anchor === "overlay" && atStart));
    }
    // Visibility can be overridden for a separately anchored fixed descendant.
    // Apply it to each recorded node so explicit visibility:visible children do
    // not leak through hidden wrappers. Group opacity would hide nested footers.
    const owners = new Map();
    for (const [node, original] of session.floatingVisibility) {
      if (!node.isConnected) continue;
      let owner = node;
      const ancestors = [];
      while (owner && !showing.has(owner) && !owners.has(owner)) {
        ancestors.push(owner);
        owner = composedParent(owner);
      }
      if (owner && !showing.has(owner)) owner = owners.get(owner);
      for (const ancestor of ancestors) owners.set(ancestor, owner);
      const visible = (!owner || showing.get(owner)) && original.visible && !session.surfaceSnapshots.has(node);
      rememberStyle(node, "visibility", visible ? "visible" : "hidden", "important");
    }
  }

  async function scrollTarget(request) {
    if (!session || session.id !== request.sessionId) throw new Error("Capture session expired and the page was restored.");
    session.lastHeartbeat = Date.now();
    const maxX = Math.max(0, request.extent.width - request.viewport.width);
    const maxY = Math.max(0, request.extent.height - request.viewport.height);
    setFloatingVisibility(request.x <= 0.5 && request.y <= 0.5, request.x >= maxX - 0.5 && request.y >= maxY - 0.5);

    if (session.fixedTarget) {
      // Fixed elements do not move with window scroll — capture in place.
    } else if (session.kind === "document" || session.kind === "region" || session.kind === "page-element") {
      const target = session.kind === "document" ? { left: 0, top: 0 } : session.targetRect;
      window.scrollTo(target.left + request.x, target.top + request.y);
    } else if (session.kind === "self-scroller") {
      const scale = ownerScale(session.owner);
      session.owner.scrollTo(request.x / scale.x, request.y / scale.y);
    } else {
      const scale = ownerScale(session.owner);
      session.owner.scrollTo(session.ownerOffset.left + request.x / scale.x, session.ownerOffset.top + request.y / scale.y);
    }

    const result = await waitForStable(request.settleTimeoutMs);
    result.semantics = semanticSnapshot(result.metrics);
    return result;
  }

  async function beginPicker(mode, frameInfo = null, pickerId = null) {
    if (pickerId && cancelledPickerIds.has(pickerId)) return { started: false, cancelled: true };
    pickerCleanup?.();
    activePickerId = pickerId;
    await restore("picker-started");
    savePickerState();
    if (mode === "region") startRegionPicker();
    else startElementPicker(mode, frameInfo);
    return { started: true };
  }

  // A picker overlay must be modal: no press, click, or context menu may reach the
  // page underneath, or the page mutates mid-capture. Pointer events are only
  // stopped, not cancelled — cancelling pointerdown would suppress the
  // compatibility mouse events the pickers themselves handle.
  function swallowPageEvents(except = []) {
    const types = ["pointerdown", "pointerup", "mousedown", "mouseup", "click", "dblclick", "auxclick", "contextmenu"];
    const handlers = types.filter((type) => !except.includes(type)).map((type) => {
      const handler = (event) => {
        if (!type.startsWith("pointer")) event.preventDefault();
        event.stopPropagation();
      };
      addEventListener(type, handler, true);
      return { type, handler };
    });
    // The browser dispatches the gesture's trailing `click` AFTER mouseup, so tearing
    // the shield down synchronously inside a mouseup/click handler would leak that
    // click to the page. Defer removal to the next task.
    return () => {
      setTimeout(() => { for (const entry of handlers) removeEventListener(entry.type, entry.handler, true); }, 0);
    };
  }

  function cancelPicker(pickerId = null) {
    if (pickerId) cancelledPickerIds.add(pickerId);
    // Bound tombstones for a long-lived page; capture IDs are never reused.
    if (cancelledPickerIds.size > 256) cancelledPickerIds.delete(cancelledPickerIds.values().next().value);
    if (pickerId && pickerId !== activePickerId) return { cancelled: false };
    pickerCleanup?.();
    activePickerId = null;
    restorePickerState();
    return { cancelled: true };
  }

  function pickerChrome(text) {
    const outline = document.createElement("div");
    const tip = document.createElement("div");
    outline.style.cssText = `position:fixed;display:none;pointer-events:none;z-index:${PICKER_Z};border:2px solid #0a84ff;background:rgba(10,132,255,.12);box-sizing:border-box`;
    tip.style.cssText = `position:fixed;left:12px;top:12px;pointer-events:none;z-index:${PICKER_Z};padding:7px 10px;border-radius:6px;background:#111;color:white;font:12px/1.35 system-ui,sans-serif;box-shadow:0 2px 10px #0006`;
    tip.textContent = text;
    outline.dataset.winshotPicker = "outline";
    tip.dataset.winshotPicker = "tip";
    document.documentElement.append(outline, tip);
    return { outline, tip };
  }

  function sendPickerResult(payload) {
    const pickerId = activePickerId;
    activePickerId = null;
    chrome.runtime.sendMessage({ type: "WINSHOT_PICK_RESULT", ...payload, pickerId }).catch(() => {});
  }

  function startElementPicker(mode = "element", frameInfo = null) {
    const frameMode = mode === "frame";
    const { outline, tip } = pickerChrome("Click an element · Hold Ctrl for the exact node · Yellow means hidden scrollable content · Esc cancels");
    if (frameMode) {
      const blocked = frameInfo?.inaccessible
        ? ` · ${frameInfo.inaccessible} frame(s) are still loading or restricted`
        : "";
      tip.textContent = window === top
        ? `Click inside the frame you want to capture${blocked} · Esc cancels`
        : "Click anywhere to capture this entire scrolling frame · Esc cancels";
    }
    const unswallow = swallowPageEvents();
    let current = null;

    const chooseCandidate = (event) => {
      const path = event.composedPath?.() || [];
      let candidate = path.find((node) => node instanceof Element && node !== outline && node !== tip) || event.target;
      if (!(candidate instanceof Element)) return null;
      if (!event.ctrlKey) {
        let scrollCandidate = candidate;
        while (scrollCandidate && scrollCandidate !== document.body && !isScrollable(scrollCandidate).any) scrollCandidate = composedParent(scrollCandidate);
        if (scrollCandidate && scrollCandidate !== document.body) candidate = scrollCandidate;
      }
      return candidate;
    };
    const move = (event) => {
      if (frameMode && window !== top) {
        // The whole frame is the target — outline the entire frame viewport.
        outline.style.display = "block";
        outline.style.left = "0px";
        outline.style.top = "0px";
        outline.style.width = `${innerWidth}px`;
        outline.style.height = `${innerHeight}px`;
        return;
      }
      current = chooseCandidate(event);
      if (!current) return;
      const rect = current.getBoundingClientRect();
      const scrollable = isScrollable(current).any;
      outline.style.display = "block";
      outline.style.left = `${rect.left}px`;
      outline.style.top = `${rect.top}px`;
      outline.style.width = `${rect.width}px`;
      outline.style.height = `${rect.height}px`;
      outline.style.borderColor = scrollable ? "#f4bd00" : "#0a84ff";
      outline.style.background = scrollable ? "rgba(244,189,0,.15)" : "rgba(10,132,255,.12)";
    };
    const click = (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (frameMode) {
        if (window === top) {
          // Clicks over an accessible frame land in that frame, never here. Landing
          // here means the click hit the top page — help instead of nagging: a
          // scrollable element is captured directly, like the element picker would.
          const candidate = chooseCandidate(event);
          if (candidate && candidate !== document.body && candidate !== document.documentElement && isScrollable(candidate).any) {
            const before = candidate.getAttribute("data-winshot-target");
            const token = crypto.randomUUID();
            candidate.setAttribute("data-winshot-target", token);
            cleanup();
            sendPickerResult({ mode: "element", target: { token, attributeBefore: before }, title: candidate.tagName.toLowerCase() });
            return;
          }
          tip.textContent = candidate?.closest?.("iframe, frame, object, embed")
            ? "This frame can't be captured (cross-origin or restricted). Grant site access or use Select an element."
            : "That isn't a frame or scrolling area. Click inside a frame, or press Esc and use Select an element.";
          return;
        }
        cleanup();
        sendPickerResult({ mode: "full-page", target: { frameDocument: true }, title: document.title || "Frame" });
        return;
      }
      current = chooseCandidate(event);
      if (!current) return;
      const before = current.getAttribute("data-winshot-target");
      const token = crypto.randomUUID();
      current.setAttribute("data-winshot-target", token);
      cleanup();
      sendPickerResult({ mode: "element", target: { token, attributeBefore: before }, title: current.tagName.toLowerCase() });
    };
    const key = (event) => {
      if (event.key === "Escape") {
        cleanup();
        restorePickerState();
        sendPickerResult({ cancelled: true });
      }
    };
    const cleanup = () => {
      unswallow();
      removeEventListener("mousemove", move, true);
      removeEventListener("click", click, true);
      removeEventListener("keydown", key, true);
      outline.remove(); tip.remove();
      pickerCleanup = null;
    };
    pickerCleanup = cleanup;
    addEventListener("mousemove", move, true);
    addEventListener("click", click, true);
    addEventListener("keydown", key, true);
  }

  function startRegionPicker() {
    const { outline, tip } = pickerChrome("Drag to select · Ctrl+click captures an element · Alt selects full page width · Ctrl while dragging locks a square · Esc cancels");
    // FireShot-style chrome: dimmed page, dashed crosshair guides, Ctrl element highlight.
    const dim = document.createElement("div");
    dim.style.cssText = `position:fixed;inset:0;pointer-events:none;z-index:${PICKER_Z - 2};background:rgba(0,0,0,.3)`;
    dim.dataset.winshotPicker = "dim";
    const inputPlane = document.createElement("div");
    inputPlane.style.cssText = `position:fixed;inset:0;pointer-events:auto;z-index:${PICKER_Z - 3};cursor:crosshair`;
    inputPlane.dataset.winshotPicker = "input";
    const guideH = document.createElement("div");
    const guideV = document.createElement("div");
    guideH.style.cssText = `position:fixed;left:0;width:100vw;height:0;display:none;pointer-events:none;z-index:${PICKER_Z - 1};border-top:1px dashed rgba(255,255,255,.85)`;
    guideV.style.cssText = `position:fixed;top:0;height:100vh;width:0;display:none;pointer-events:none;z-index:${PICKER_Z - 1};border-left:1px dashed rgba(255,255,255,.85)`;
    guideH.dataset.winshotPicker = "guide";
    guideV.dataset.winshotPicker = "guide";
    document.documentElement.append(inputPlane, dim, guideH, guideV);
    const ownNodes = [outline, tip, inputPlane, dim, guideH, guideV];
    const unswallow = swallowPageEvents();
    let dragging = false;
    let start = null;
    let currentClient = null;
    let currentModifiers = { altKey: false, ctrlKey: false };
    let hoverTarget = null;
    let autoTimer = 0;

    const elementUnderCursor = (event) => {
      inputPlane.style.pointerEvents = "none";
      let candidate;
      try {
        candidate = document.elementFromPoint(event.clientX, event.clientY);
        while (candidate?.shadowRoot) {
          const inner = candidate.shadowRoot.elementFromPoint(event.clientX, event.clientY);
          if (!inner || inner === candidate) break;
          candidate = inner;
        }
      } finally { inputPlane.style.pointerEvents = "auto"; }
      return candidate instanceof Element && !ownNodes.includes(candidate) ? candidate : null;
    };
    const showElementHighlight = (element) => {
      const rect = element.getBoundingClientRect();
      const scrollable = isScrollable(element).any;
      outline.style.display = "block";
      outline.style.boxShadow = "none";
      outline.style.left = `${rect.left}px`;
      outline.style.top = `${rect.top}px`;
      outline.style.width = `${rect.width}px`;
      outline.style.height = `${rect.height}px`;
      outline.style.borderColor = scrollable ? "#f4bd00" : "#1687ff";
      outline.style.background = scrollable ? "rgba(244,189,0,.15)" : "rgba(22,135,255,.12)";
    };

    const documentPoint = (event) => ({ x: event.clientX + window.scrollX, y: event.clientY + window.scrollY });
    const selectionRect = (end, modifiers) => {
      let deltaX = end.x - start.x;
      let deltaY = end.y - start.y;
      if (modifiers.ctrlKey && !modifiers.altKey) {
        const edge = Math.max(Math.abs(deltaX), Math.abs(deltaY));
        deltaX = Math.sign(deltaX || 1) * edge;
        deltaY = Math.sign(deltaY || 1) * edge;
      }
      let left = Math.min(start.x, start.x + deltaX);
      let right = Math.max(start.x, start.x + deltaX);
      if (modifiers.altKey) {
        left = 0;
        right = Math.max(document.documentElement.scrollWidth, document.body?.scrollWidth || 0, innerWidth);
      }
      const top = Math.min(start.y, start.y + deltaY);
      const bottom = Math.max(start.y, start.y + deltaY);
      return { left, top, width: right - left, height: bottom - top };
    };
    const update = () => {
      if (!dragging || !currentClient) return;
      const now = { x: currentClient.x + window.scrollX, y: currentClient.y + window.scrollY };
      const rect = selectionRect(now, currentModifiers);
      outline.style.display = "block";
      outline.style.borderColor = "#1687ff";
      outline.style.background = "rgba(22,135,255,.12)";
      // The selection stays bright while everything around it dims.
      outline.style.boxShadow = "0 0 0 200000px rgba(0,0,0,.3)";
      outline.style.left = `${rect.left - window.scrollX}px`;
      outline.style.top = `${rect.top - window.scrollY}px`;
      outline.style.width = `${rect.width}px`;
      outline.style.height = `${rect.height}px`;
    };
    const down = (event) => {
      if (event.button !== 0) return;
      event.preventDefault(); event.stopPropagation();
      dragging = true;
      guideH.style.display = "none";
      guideV.style.display = "none";
      dim.style.display = "none";
      start = documentPoint(event);
      currentClient = { x: event.clientX, y: event.clientY };
      currentModifiers = { altKey: event.altKey, ctrlKey: event.ctrlKey };
      autoTimer = setInterval(() => {
        if (!dragging || !currentClient) return;
        const margin = 40;
        let dx = 0, dy = 0;
        if (currentClient.x < margin) dx = -24;
        else if (currentClient.x > innerWidth - margin) dx = 24;
        if (currentClient.y < margin) dy = -24;
        else if (currentClient.y > innerHeight - margin) dy = 24;
        if (dx || dy) { window.scrollBy(dx, dy); update(); }
      }, 16);
    };
    const move = (event) => {
      if (!dragging) {
        if (event.ctrlKey) {
          hoverTarget = elementUnderCursor(event);
          guideH.style.display = "none";
          guideV.style.display = "none";
          if (hoverTarget) showElementHighlight(hoverTarget);
        } else {
          hoverTarget = null;
          outline.style.display = "none";
          guideH.style.display = "block";
          guideV.style.display = "block";
          guideH.style.top = `${event.clientY}px`;
          guideV.style.left = `${event.clientX}px`;
        }
        return;
      }
      event.preventDefault(); event.stopPropagation();
      currentClient = { x: event.clientX, y: event.clientY };
      currentModifiers = { altKey: event.altKey, ctrlKey: event.ctrlKey };
      update();
    };
    const up = (event) => {
      if (!dragging) return;
      event.preventDefault(); event.stopPropagation();
      const end = documentPoint(event);
      const rect = selectionRect(end, {
        altKey: event.altKey || currentModifiers.altKey,
        ctrlKey: event.ctrlKey || currentModifiers.ctrlKey
      });
      if (rect.width < 4 || rect.height < 4) {
        // FireShot parity: Ctrl+click (no drag) captures the hovered element;
        // the capture engine scrolls a scrollable one fully on its own.
        const element = event.ctrlKey ? (hoverTarget || elementUnderCursor(event)) : null;
        cleanup();
        if (element) {
          const before = element.getAttribute("data-winshot-target");
          const token = crypto.randomUUID();
          element.setAttribute("data-winshot-target", token);
          sendPickerResult({ mode: "element", target: { token, attributeBefore: before }, title: element.tagName.toLowerCase() });
        } else {
          restorePickerState();
          sendPickerResult({ cancelled: true });
        }
        return;
      }
      cleanup();
      sendPickerResult({ mode: "region", target: { rect } });
    };
    const key = (event) => {
      if (event.key === "Escape") { cleanup(); restorePickerState(); sendPickerResult({ cancelled: true }); }
    };
    const cleanup = () => {
      unswallow();
      clearInterval(autoTimer);
      removeEventListener("mousedown", down, true);
      removeEventListener("mousemove", move, true);
      removeEventListener("mouseup", up, true);
      removeEventListener("keydown", key, true);
      outline.remove(); tip.remove(); inputPlane.remove(); dim.remove(); guideH.remove(); guideV.remove();
      pickerCleanup = null;
    };
    pickerCleanup = cleanup;
    addEventListener("mousedown", down, true);
    addEventListener("mousemove", move, true);
    addEventListener("mouseup", up, true);
    addEventListener("keydown", key, true);
  }

  chrome.runtime.onMessage.addListener((request, _sender, sendResponse) => {
    const respond = async () => {
      switch (request.type) {
        case "WINSHOT_PREPARE": return await prepare(request);
        case "WINSHOT_SCROLL": return await scrollTarget(request);
        case "WINSHOT_METRICS": return currentMetrics();
        case "WINSHOT_HEARTBEAT":
          if (session?.id === request.sessionId) session.lastHeartbeat = Date.now();
          return { alive: Boolean(session) };
        case "WINSHOT_RESTORE": return restore(request.reason || "complete");
        case "WINSHOT_LOCATE_FRAME": return await locateFrameHost(request);
        case "WINSHOT_ANNOUNCE_FRAME":
          if (window === top) throw new Error("The top page cannot announce itself as a child frame.");
          parent.postMessage({ source: "winshot-frame", token: request.frameToken }, "*");
          return { announced: true };
        case "WINSHOT_RESTORE_FRAME_HOST": return restoreFrameHost();
        case "WINSHOT_HEARTBEAT_FRAME_HOST":
          if (frameHostSession?.id === request.sessionId) frameHostSession.lastHeartbeat = Date.now();
          return { alive: Boolean(frameHostSession) };
        case "WINSHOT_VISIBLE_SEMANTICS": return visibleSemantics();
        case "WINSHOT_FRAME_INVENTORY": return {
          count: openRoots().reduce((count, root) => count + [...root.querySelectorAll("iframe,frame")].filter(node => node.clientWidth > 100 && node.clientHeight > 50).length, 0)
        };
        case "WINSHOT_START_PICKER": return await beginPicker(request.mode, request.frameInfo || null, request.pickerId || null);
        case "WINSHOT_CANCEL_PICKER": return cancelPicker(request.pickerId || null);
        default: return undefined;
      }
    };
    respond().then(sendResponse, (error) => sendResponse({ error: error?.message || String(error) }));
    return true;
  });
})();
