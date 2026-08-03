(() => {
  if (globalThis.__winshotCaptureScriptInstalled) return;
  globalThis.__winshotCaptureScriptInstalled = true;

  const WATCHDOG_MS = 9000;
  const PICKER_Z = "2147483647";
  let session = null;
  let frameHostSession = null;
  let pickerCleanup = null;
  let pendingPickerState = null;

  const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
  const nextPaint = () => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));

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

  function nearestScrollOwner(element) {
    for (let node = element?.parentElement; node && node !== document.body; node = node.parentElement) {
      if (isScrollable(node).any) return node;
    }
    return scrollingElement();
  }

  function rememberStyle(element, property, value, priority = "important") {
    if (!session) return;
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

  function restore(reason = "requested") {
    if (!session) return { restored: true, reason };
    const current = session;
    session = null;
    clearInterval(current.watchdog);
    current.observer?.disconnect();

    for (const [element, properties] of [...current.changedStyles].reverse()) {
      if (!(element instanceof Element)) continue;
      for (const [property, original] of properties) {
        if (original.value) element.style.setProperty(property, original.value, original.priority);
        else element.style.removeProperty(property);
      }
    }
    current.freezeStyle?.remove();

    if (current.owner instanceof Element && current.owner !== scrollingElement()) {
      current.owner.scrollTo(current.ownerScroll.left, current.ownerScroll.top);
    }
    window.scrollTo(current.windowScroll.left, current.windowScroll.top);

    try {
      const selection = getSelection();
      selection?.removeAllRanges();
      for (const range of current.selection) selection?.addRange(range);
      current.activeElement?.focus?.({ preventScroll: true });
    } catch {
      // A page may have removed the selected/focused node while capture was running.
    }

    if (current.targetElement && current.targetToken) {
      if (current.targetAttributeBefore === null) current.targetElement.removeAttribute("data-winshot-target");
      else current.targetElement.setAttribute("data-winshot-target", current.targetAttributeBefore);
    }
    return { restored: true, reason };
  }

  function savePickerState() {
    clearTimeout(pendingPickerState?.timer);
    const state = {
      windowScroll: { left: window.scrollX, top: window.scrollY },
      activeElement: document.activeElement,
      selection: snapshotSelection(),
      timer: 0
    };
    state.timer = setTimeout(() => restorePickerState(), WATCHDOG_MS);
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
          height: visual?.height ?? current.document.documentElement.clientHeight
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
    window.scrollTo(saved.scroll.left, saved.scroll.top);
    try { saved.activeElement?.focus?.({ preventScroll: true }); } catch { /* The page may have replaced it. */ }
    return { restored: true };
  }

  async function locateFrameHost(request) {
    restoreFrameHost();
    const candidates = [...document.querySelectorAll("iframe,frame")].filter((frame) => {
      try { return new URL(frame.src, location.href).href === request.frameUrl; } catch { return false; }
    });
    if (candidates.length !== 1) {
      throw new Error(candidates.length ? "More than one matching frame is present; WinShot cannot safely choose one." : "The selected frame is nested or no longer present in the top page.");
    }
    const frame = candidates[0];
    frameHostSession = {
      id: request.sessionId,
      scroll: { left: window.scrollX, top: window.scrollY },
      activeElement: document.activeElement,
      lastHeartbeat: Date.now(),
      watchdog: 0
    };
    frameHostSession.watchdog = setInterval(() => {
      if (frameHostSession && Date.now() - frameHostSession.lastHeartbeat > WATCHDOG_MS) restoreFrameHost();
    }, 1000);
    frame.scrollIntoView({ block: "center", inline: "center", behavior: "instant" });
    await nextPaint();
    const rect = frame.getBoundingClientRect();
    const view = viewport();
    const left = rect.left + frame.clientLeft;
    const topOffset = rect.top + frame.clientTop;
    if (left < 0 || topOffset < 0 || left + frame.clientWidth > view.width || topOffset + frame.clientHeight > view.height) {
      restoreFrameHost();
      throw new Error("The frame viewport is larger than the visible browser area and cannot be captured safely.");
    }
    return { located: true, left, top: topOffset, width: frame.clientWidth, height: frame.clientHeight, topViewport: view };
  }

  function ownerClientRect(owner) {
    if (owner === scrollingElement()) {
      const view = viewport();
      return { left: 0, top: 0, width: view.width, height: view.height };
    }
    const rect = owner.getBoundingClientRect();
    return {
      left: rect.left + owner.clientLeft,
      top: rect.top + owner.clientTop,
      width: owner.clientWidth,
      height: owner.clientHeight
    };
  }

  function visibleClientRect(element) {
    let result = ownerClientRect(element);
    for (let ancestor = element.parentElement; ancestor && ancestor !== document.body; ancestor = ancestor.parentElement) {
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

    if (session.kind === "document" || session.kind === "region" || session.kind === "page-element") {
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
      width = owner.scrollWidth;
      height = owner.scrollHeight;
      const left = Math.max(0, rect.left);
      const top = Math.max(0, rect.top);
      const right = Math.min(view.width, rect.left + rect.width);
      const bottom = Math.min(view.height, rect.top + rect.height);
      clip = { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
      destX = owner.scrollLeft + Math.max(0, -rect.left);
      destY = owner.scrollTop + Math.max(0, -rect.top);
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
      url: location.href,
      frame: frameContext()
    };
  }

  async function waitForStable(timeoutMs) {
    const start = performance.now();
    let previous = "";
    let repeats = 0;
    while (performance.now() - start < timeoutMs) {
      await nextPaint();
      const metrics = currentMetrics();
      const signature = JSON.stringify([
        Math.round(metrics.width * 10), Math.round(metrics.height * 10),
        Math.round(metrics.reachedX * 10), Math.round(metrics.reachedY * 10),
        Math.round(metrics.clip.width * 10), Math.round(metrics.clip.height * 10),
        metrics.mutationCount
      ]);
      if (signature === previous) repeats++;
      else repeats = 0;
      if (repeats >= 1) return { stable: true, metrics };
      previous = signature;
      await sleep(60);
    }
    return { stable: false, metrics: currentMetrics() };
  }

  function semanticSnapshot(metrics, limit = 5000) {
    const clip = metrics.clip;
    const right = clip.left + clip.width;
    const bottom = clip.top + clip.height;
    const text = [];
    const links = [];
    const seenText = new Set();
    const seenLinks = new Set();

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

    const walker = document.createTreeWalker(document.body || document.documentElement, NodeFilter.SHOW_TEXT);
    while (text.length < limit) {
      const node = walker.nextNode();
      if (!node) break;
      const value = node.nodeValue?.replace(/\s+/g, " ").trim();
      if (!value || !(node.parentElement instanceof Element)) continue;
      const style = getComputedStyle(node.parentElement);
      if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) continue;
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

    for (const anchor of document.querySelectorAll("a[href]")) {
      if (links.length >= 2000) break;
      const style = getComputedStyle(anchor);
      if (style.display === "none" || style.visibility === "hidden" || Number(style.opacity) === 0) continue;
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
    return { text, links };
  }

  function visibleSemantics() {
    const view = viewport();
    const metrics = {
      clip: { left: view.offsetLeft, top: view.offsetTop, width: view.width, height: view.height },
      destX: 0,
      destY: 0
    };
    return { metrics: { width: view.width, height: view.height }, semantics: semanticSnapshot(metrics) };
  }

  function collectWarnings() {
    const warnings = [];
    let inaccessibleFrames = 0;
    for (const frame of document.querySelectorAll("iframe,frame")) {
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

  function classifyTarget(request) {
    if (request.mode === "full-page") {
      return { kind: "document", owner: scrollingElement(), targetRect: { left: 0, top: 0, ...documentSize() } };
    }
    if (request.mode === "region") {
      return { kind: "region", owner: scrollingElement(), targetRect: request.target.rect };
    }
    if (request.mode === "element") {
      const element = document.querySelector(`[data-winshot-target="${CSS.escape(request.target.token)}"]`);
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
      return {
        kind: "ancestor-scroller",
        owner,
        targetElement: element,
        ownerOffset: {
          left: owner.scrollLeft + rect.left - ownerRect.left,
          top: owner.scrollTop + rect.top - ownerRect.top
        },
        targetRect: { left: 0, top: 0, width: rect.width, height: rect.height }
      };
    }
    throw new Error(`Unsupported capture mode: ${request.mode}`);
  }

  function freezePage(classification, request) {
    const freezeStyle = document.createElement("style");
    freezeStyle.dataset.winshotCapture = request.sessionId;
    freezeStyle.textContent = `
      html { scroll-behavior: auto !important; scroll-snap-type: none !important; overflow-anchor: none !important; }
      *, *::before, *::after {
        animation-play-state: paused !important;
        scroll-behavior: auto !important;
        scroll-snap-type: none !important;
        overflow-anchor: none !important;
        caret-color: transparent !important;
      }
    `;
    (document.head || document.documentElement).append(freezeStyle);
    session.freezeStyle = freezeStyle;

    if (classification.owner instanceof Element) {
      rememberStyle(classification.owner, "scroll-behavior", "auto");
      rememberStyle(classification.owner, "scroll-snap-type", "none");
      rememberStyle(classification.owner, "overflow-anchor", "none");
    }

    const nodes = document.querySelectorAll("body *");
    const cap = Math.min(nodes.length, 25000);
    for (let i = 0; i < cap; i++) {
      const node = nodes[i];
      const position = getComputedStyle(node).position;
      if (position !== "fixed" && position !== "sticky") continue;
      const rect = node.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) continue;
      session.floating.push({ node, anchor: rect.top + rect.height / 2 < innerHeight / 2 ? "top" : "bottom" });
    }
  }

  async function prepare(request) {
    restore("superseded");
    const classification = classifyTarget(request);
    const owner = classification.owner;
    const pickerState = pendingPickerState;
    pendingPickerState = null;
    clearTimeout(pickerState?.timer);
    session = {
      id: request.sessionId,
      kind: classification.kind,
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
      freezeStyle: null,
      floating: [],
      mutationCount: 0,
      lastHeartbeat: Date.now(),
      watchdog: 0,
      observer: null
    };

    freezePage(classification, request);
    session.observer = new MutationObserver(() => { if (session) session.mutationCount++; });
    session.observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true });
    session.watchdog = setInterval(() => {
      if (session && Date.now() - session.lastHeartbeat > WATCHDOG_MS) restore("watchdog");
    }, 1000);

    await nextPaint();
    const metrics = currentMetrics();
    const warnings = collectWarnings();
    if (classification.kind === "ancestor-scroller") {
      warnings.push("The selected element is inside a separate scrolling panel; WinShot will scroll that panel and validate coverage.");
    }
    return { metrics, warnings };
  }

  function setFloatingVisibility(atStart, atEnd) {
    if (!session) return;
    for (const item of session.floating) {
      const show = (item.anchor === "top" && atStart) || (item.anchor === "bottom" && atEnd);
      rememberStyle(item.node, "visibility", show ? "visible" : "hidden");
    }
  }

  async function scrollTarget(request) {
    if (!session || session.id !== request.sessionId) throw new Error("Capture session expired and the page was restored.");
    session.lastHeartbeat = Date.now();
    const maxX = Math.max(0, request.extent.width - request.viewport.width);
    const maxY = Math.max(0, request.extent.height - request.viewport.height);
    setFloatingVisibility(request.x <= 0.5 && request.y <= 0.5, request.x >= maxX - 0.5 && request.y >= maxY - 0.5);

    if (session.kind === "document" || session.kind === "region" || session.kind === "page-element") {
      const target = session.kind === "document" ? { left: 0, top: 0 } : session.targetRect;
      window.scrollTo(target.left + request.x, target.top + request.y);
    } else if (session.kind === "self-scroller") {
      session.owner.scrollTo(request.x, request.y);
    } else {
      session.owner.scrollTo(session.ownerOffset.left + request.x, session.ownerOffset.top + request.y);
    }

    const result = await waitForStable(request.settleTimeoutMs);
    result.semantics = semanticSnapshot(result.metrics);
    return result;
  }

  async function beginPicker(mode) {
    pickerCleanup?.();
    restore("picker-started");
    savePickerState();
    if (mode === "region") startRegionPicker();
    else startElementPicker(mode);
    return { started: true };
  }

  function cancelPicker() {
    pickerCleanup?.();
    restorePickerState();
    return { cancelled: true };
  }

  function pickerChrome(text) {
    const outline = document.createElement("div");
    const tip = document.createElement("div");
    outline.style.cssText = `position:fixed;display:none;pointer-events:none;z-index:${PICKER_Z};border:2px solid #1687ff;background:rgba(22,135,255,.12);box-sizing:border-box`;
    tip.style.cssText = `position:fixed;left:12px;top:12px;pointer-events:none;z-index:${PICKER_Z};padding:7px 10px;border-radius:6px;background:#111;color:white;font:12px/1.35 system-ui,sans-serif;box-shadow:0 2px 10px #0006`;
    tip.textContent = text;
    outline.dataset.winshotPicker = "outline";
    tip.dataset.winshotPicker = "tip";
    document.documentElement.append(outline, tip);
    return { outline, tip };
  }

  function sendPickerResult(payload) {
    chrome.runtime.sendMessage({ type: "WINSHOT_PICK_RESULT", ...payload }).catch(() => {});
  }

  function startElementPicker(mode = "element") {
    const frameMode = mode === "frame";
    const { outline, tip } = pickerChrome("Click an element · Hold Ctrl for the exact node · Yellow means hidden scrollable content · Esc cancels");
    if (frameMode) tip.textContent = window === top
      ? "Click inside the frame you want to capture · Esc cancels"
      : "Click anywhere to capture this entire scrolling frame · Esc cancels";
    let current = null;

    const chooseCandidate = (event) => {
      const path = event.composedPath?.() || [];
      let candidate = path.find((node) => node instanceof Element && node !== outline && node !== tip) || event.target;
      if (!(candidate instanceof Element)) return null;
      if (!event.ctrlKey) {
        let scrollCandidate = candidate;
        while (scrollCandidate && scrollCandidate !== document.body && !isScrollable(scrollCandidate).any) scrollCandidate = scrollCandidate.parentElement;
        if (scrollCandidate && scrollCandidate !== document.body) candidate = scrollCandidate;
      }
      return candidate;
    };
    const move = (event) => {
      current = chooseCandidate(event);
      if (!current) return;
      const rect = current.getBoundingClientRect();
      const scrollable = isScrollable(current).any;
      outline.style.display = "block";
      outline.style.left = `${rect.left}px`;
      outline.style.top = `${rect.top}px`;
      outline.style.width = `${rect.width}px`;
      outline.style.height = `${rect.height}px`;
      outline.style.borderColor = scrollable ? "#f4bd00" : "#1687ff";
      outline.style.background = scrollable ? "rgba(244,189,0,.15)" : "rgba(22,135,255,.12)";
    };
    const click = (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (frameMode) {
        if (window === top) {
          tip.textContent = "Click inside the frame, not its border.";
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
    const { outline, tip } = pickerChrome("Drag a rectangle · Move to an edge to auto-scroll · Esc cancels");
    let dragging = false;
    let start = null;
    let currentClient = null;
    let autoTimer = 0;

    const documentPoint = (event) => ({ x: event.clientX + window.scrollX, y: event.clientY + window.scrollY });
    const update = () => {
      if (!dragging || !currentClient) return;
      const now = { x: currentClient.x + window.scrollX, y: currentClient.y + window.scrollY };
      const left = Math.min(start.x, now.x);
      const top = Math.min(start.y, now.y);
      const right = Math.max(start.x, now.x);
      const bottom = Math.max(start.y, now.y);
      outline.style.display = "block";
      outline.style.left = `${left - window.scrollX}px`;
      outline.style.top = `${top - window.scrollY}px`;
      outline.style.width = `${right - left}px`;
      outline.style.height = `${bottom - top}px`;
    };
    const down = (event) => {
      if (event.button !== 0) return;
      event.preventDefault(); event.stopPropagation();
      dragging = true;
      start = documentPoint(event);
      currentClient = { x: event.clientX, y: event.clientY };
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
      if (!dragging) return;
      event.preventDefault(); event.stopPropagation();
      currentClient = { x: event.clientX, y: event.clientY };
      update();
    };
    const up = (event) => {
      if (!dragging) return;
      event.preventDefault(); event.stopPropagation();
      const end = documentPoint(event);
      const rect = {
        left: Math.min(start.x, end.x), top: Math.min(start.y, end.y),
        width: Math.abs(end.x - start.x), height: Math.abs(end.y - start.y)
      };
      cleanup();
      if (rect.width < 4 || rect.height < 4) { restorePickerState(); sendPickerResult({ cancelled: true }); }
      else sendPickerResult({ mode: "region", target: { rect } });
    };
    const key = (event) => {
      if (event.key === "Escape") { cleanup(); restorePickerState(); sendPickerResult({ cancelled: true }); }
    };
    const cleanup = () => {
      clearInterval(autoTimer);
      removeEventListener("mousedown", down, true);
      removeEventListener("mousemove", move, true);
      removeEventListener("mouseup", up, true);
      removeEventListener("keydown", key, true);
      outline.remove(); tip.remove();
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
        case "WINSHOT_RESTORE_FRAME_HOST": return restoreFrameHost();
        case "WINSHOT_HEARTBEAT_FRAME_HOST":
          if (frameHostSession?.id === request.sessionId) frameHostSession.lastHeartbeat = Date.now();
          return { alive: Boolean(frameHostSession) };
        case "WINSHOT_VISIBLE_SEMANTICS": return visibleSemantics();
        case "WINSHOT_START_PICKER": return await beginPicker(request.mode);
        case "WINSHOT_CANCEL_PICKER": return cancelPicker();
        default: return undefined;
      }
    };
    respond().then(sendResponse, (error) => sendResponse({ error: error?.message || String(error) }));
    return true;
  });
})();
