// The original capture remains tiled and untouched. Geometry is stored in source
// pixels; previews and every export share this document instead of editing a
// downscaled screenshot or allocating a page-sized canvas.
const SVG = "http://www.w3.org/2000/svg";
const clone = (value) => structuredClone(value);
const bounds = (shape) => {
  if (shape.type === "pen") {
    let left = Infinity,
      top = Infinity,
      right = -Infinity,
      bottom = -Infinity;
    for (const p of shape.points) {
      left = Math.min(left, p.x);
      top = Math.min(top, p.y);
      right = Math.max(right, p.x);
      bottom = Math.max(bottom, p.y);
    }
    return { x: left, y: top, width: right - left, height: bottom - top };
  }
  return {
    x: Math.min(shape.x, shape.x + shape.width),
    y: Math.min(shape.y, shape.y + shape.height),
    width: Math.abs(shape.width),
    height: Math.abs(shape.height),
  };
};
const NEUTRAL_EFFECTS = {
  brightness: 100,
  contrast: 100,
  saturation: 100,
  grayscale: 0,
  sepia: 0,
};
export function imageOutputSize(doc) {
  const rotated = (doc.transform?.rotation || 0) % 180 !== 0;
  return {
    width: doc.transform?.width || (rotated ? doc.crop.height : doc.crop.width),
    height:
      doc.transform?.height || (rotated ? doc.crop.width : doc.crop.height),
  };
}
function imageTransform(doc) {
  return {
    rotation: 0,
    flipX: false,
    flipY: false,
    ...imageOutputSize(doc),
    ...doc.transform,
  };
}
function imageEffects(doc) {
  return { ...NEUTRAL_EFFECTS, ...doc.effects };
}
function effectFilter(doc) {
  const e = imageEffects(doc);
  if (Object.entries(NEUTRAL_EFFECTS).every(([key, value]) => e[key] === value))
    return "none";
  return `brightness(${e.brightness}%) contrast(${e.contrast}%) saturate(${e.saturation}%) grayscale(${e.grayscale}%) sepia(${e.sepia}%)`;
}
function outputMatrix(doc) {
  const c = doc.crop,
    t = imageTransform(doc),
    rotated = t.rotation % 180 !== 0,
    rw = rotated ? c.height : c.width,
    rh = rotated ? c.width : c.height;
  const rotate =
    t.rotation === 90
      ? new DOMMatrix([0, 1, -1, 0, c.height, 0])
      : t.rotation === 180
        ? new DOMMatrix([-1, 0, 0, -1, c.width, c.height])
        : t.rotation === 270
          ? new DOMMatrix([0, -1, 1, 0, 0, c.width])
          : new DOMMatrix();
  return new DOMMatrix()
    .scale(t.width / rw, t.height / rh)
    .translate(t.flipX ? rw : 0, t.flipY ? rh : 0)
    .scale(t.flipX ? -1 : 1, t.flipY ? -1 : 1)
    .multiply(rotate);
}
const snapshot = (doc) =>
  clone({
    crop: doc.crop,
    objects: doc.objects,
    transform: imageTransform(doc),
    effects: imageEffects(doc),
  });
function restoreSnapshot(doc, value) {
  Object.assign(doc, value);
  doc.transform = imageTransform(value);
  doc.effects = imageEffects(value);
}
function svg(tag, attributes = {}) {
  const node = document.createElementNS(SVG, tag);
  for (const [key, value] of Object.entries(attributes))
    node.setAttribute(key, String(value));
  return node;
}
function arrowPoints(s) {
  const x = s.x + s.width,
    y = s.y + s.height,
    angle = Math.atan2(s.height, s.width),
    size = Math.max(12, s.stroke * 4);
  return [
    {
      x: x - size * Math.cos(angle - 0.45),
      y: y - size * Math.sin(angle - 0.45),
    },
    { x, y },
    {
      x: x - size * Math.cos(angle + 0.45),
      y: y - size * Math.sin(angle + 0.45),
    },
  ];
}
function redactionFill(shape) {
  return /^#[0-9a-f]{6}$/i.test(shape.redactionColor || "")
    ? shape.redactionColor
    : "#111111";
}
function drawSvg(shape) {
  const s = shape,
    b = bounds(s),
    group = svg("g", {
      "data-object-id": s.id,
      fill: "none",
      stroke: s.color,
      "stroke-width": s.stroke,
      "stroke-linecap": "round",
      "stroke-linejoin": "round",
    });
  if (s.type === "rectangle" || s.type === "redact")
    group.append(
      svg("rect", {
        ...b,
        ...(s.type === "redact"
          ? { fill: redactionFill(s), stroke: "none" }
          : {}),
      }),
    );
  else if (s.type === "ellipse")
    group.append(
      svg("ellipse", {
        cx: b.x + b.width / 2,
        cy: b.y + b.height / 2,
        rx: b.width / 2,
        ry: b.height / 2,
      }),
    );
  else if (s.type === "arrow") {
    group.append(
      svg("line", { x1: s.x, y1: s.y, x2: s.x + s.width, y2: s.y + s.height }),
    );
    group.append(
      svg("polyline", {
        points: arrowPoints(s)
          .map((p) => `${p.x},${p.y}`)
          .join(" "),
      }),
    );
  } else if (s.type === "pen")
    group.append(
      svg("polyline", {
        points: s.points.map((p) => `${p.x},${p.y}`).join(" "),
      }),
    );
  else if (s.type === "text") {
    const text = svg("text", {
      x: s.x,
      y: s.y,
      fill: s.color,
      stroke: "none",
      "font-size": s.fontSize,
      "font-family": "system-ui, Segoe UI, sans-serif",
      "dominant-baseline": "text-before-edge",
    });
    s.text.split("\n").forEach((line, i) => {
      const span = svg("tspan", { x: s.x, dy: i ? s.fontSize * 1.25 : 0 });
      span.textContent = line || " ";
      text.append(span);
    });
    group.append(text);
  }
  return group;
}
function drawCanvas(ctx, s) {
  const b = bounds(s);
  ctx.save();
  ctx.strokeStyle = s.color;
  ctx.fillStyle = s.color;
  ctx.lineWidth = s.stroke;
  ctx.lineCap = "round";
  ctx.lineJoin = "round";
  ctx.beginPath();
  if (s.type === "rectangle") ctx.rect(b.x, b.y, b.width, b.height);
  else if (s.type === "redact") {
    ctx.fillStyle = redactionFill(s);
    ctx.fillRect(b.x, b.y, b.width, b.height);
    ctx.restore();
    return;
  } else if (s.type === "ellipse")
    ctx.ellipse(
      b.x + b.width / 2,
      b.y + b.height / 2,
      b.width / 2,
      b.height / 2,
      0,
      0,
      Math.PI * 2,
    );
  else if (s.type === "arrow") {
    ctx.moveTo(s.x, s.y);
    ctx.lineTo(s.x + s.width, s.y + s.height);
    const p = arrowPoints(s);
    ctx.moveTo(p[0].x, p[0].y);
    ctx.lineTo(p[1].x, p[1].y);
    ctx.lineTo(p[2].x, p[2].y);
  } else if (s.type === "pen") {
    s.points.forEach((p, i) =>
      i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y),
    );
  } else if (s.type === "text") {
    ctx.font = `${s.fontSize}px system-ui, Segoe UI, sans-serif`;
    ctx.textBaseline = "top";
    s.text
      .split("\n")
      .forEach((line, i) =>
        ctx.fillText(line, s.x, s.y + i * s.fontSize * 1.25),
      );
    ctx.restore();
    return;
  }
  ctx.stroke();
  ctx.restore();
}

export class ImageEditor {
  constructor({ preview, stage, record, tiles, persist, notify }) {
    this.preview = preview;
    this.stage = stage;
    this.record = record;
    this.tiles = [...tiles].sort((a, b) => a.index - b.index);
    this.persist = persist;
    this.notify = notify;
    const size = record.report.dimensionsPx,
      full = { x: 0, y: 0, ...size };
    this.doc =
      record.editorDocument?.version === 1
        ? clone(record.editorDocument)
        : { version: 1, crop: full, objects: [], past: [], future: [] };
    this.doc.past ||= [];
    this.doc.future ||= [];
    this.doc.transform = imageTransform(this.doc);
    this.doc.effects = imageEffects(this.doc);
    this.tool = "select";
    this.selected = null;
    this.scale = 1;
    this.zoom = "fit";
    this.urls = [];
    this.saveChain = Promise.resolve();
    this.saveRevision = 0;
    this.persistedRevision = 0;
    this.projectRevision = 0;
    this.beforeUnload = (event) => {
      if (
        this.saveRevision <=
        Math.max(this.persistedRevision, this.projectRevision)
      )
        return;
      event.preventDefault();
      event.returnValue = "";
    };
    addEventListener("beforeunload", this.beforeUnload);
    this.content = document.createElement("div");
    this.content.className = "image-content";
    preview.replaceChildren(this.content);
    this.source = document.createElement("div");
    this.source.className = "source-tiles";
    this.content.append(this.source);
    for (const tile of this.tiles) {
      const img = document.createElement("img"),
        url = URL.createObjectURL(tile.blob);
      this.urls.push(url);
      img.src = url;
      img.alt = "";
      img.draggable = false;
      Object.assign(img.style, {
        left: `${tile.destXPx}px`,
        top: `${tile.destYPx}px`,
        width: `${tile.widthPx}px`,
        height: `${tile.heightPx}px`,
      });
      this.source.append(img);
    }
    this.overlay = svg("svg", {
      "aria-label": "Image annotation canvas",
      tabindex: "0",
      role: "application",
    });
    this.overlay.classList.add("annotation-overlay");
    this.content.append(this.overlay);
    this.bind();
    this.bindImageControls();
    this.render();
    this.resizeObserver = new ResizeObserver(() => this.layout());
    this.resizeObserver.observe(stage);
  }
  bindImageControls() {
    document.querySelector("#image-resize").addEventListener("click", () => {
      const width = Number(document.querySelector("#image-width").value),
        height = Number(document.querySelector("#image-height").value);
      if (
        !Number.isInteger(width) ||
        !Number.isInteger(height) ||
        width < 1 ||
        height < 1 ||
        width > 200000 ||
        height > 200000 ||
        width * height > 100000000
      ) {
        this.notify(
          "Choose whole pixel sizes up to 200,000 per side and 100 million pixels total.",
        );
        return;
      }
      this.cancelGesture();
      this.remember();
      Object.assign(this.doc.transform, { width, height });
      this.changed();
    });
    for (const [field, other] of [
      ["width", "height"],
      ["height", "width"],
    ])
      document
        .querySelector(`#image-${field}`)
        .addEventListener("input", () => {
          if (!document.querySelector("#image-lock-ratio").checked) return;
          const size = imageOutputSize(this.doc),
            value = Number(document.querySelector(`#image-${field}`).value);
          if (Number.isFinite(value) && value > 0)
            document.querySelector(`#image-${other}`).value = Math.max(
              1,
              Math.round((value * size[other]) / size[field]),
            );
        });
    for (const turn of [-90, 90])
      document
        .querySelector(turn < 0 ? "#image-rotate-left" : "#image-rotate-right")
        .addEventListener("click", () => {
          this.cancelGesture();
          this.remember();
          const t = this.doc.transform;
          t.rotation = (t.rotation + turn + 360) % 360;
          [t.width, t.height] = [t.height, t.width];
          [t.flipX, t.flipY] = [t.flipY, t.flipX];
          this.changed();
        });
    for (const axis of ["X", "Y"])
      document
        .querySelector(`#image-flip-${axis.toLowerCase()}`)
        .addEventListener("click", () => {
          this.cancelGesture();
          this.remember();
          this.doc.transform[`flip${axis}`] =
            !this.doc.transform[`flip${axis}`];
          this.changed();
        });
    for (const name of Object.keys(NEUTRAL_EFFECTS))
      document
        .querySelector(`#effect-${name}`)
        .addEventListener("change", () => {
          const input = document.querySelector(`#effect-${name}`),
            max = name === "grayscale" || name === "sepia" ? 100 : 200;
          this.cancelGesture();
          this.remember();
          this.doc.effects[name] = Math.max(
            0,
            Math.min(max, Number(input.value) || 0),
          );
          this.changed();
        });
    document.querySelector("#effects-reset").addEventListener("click", () => {
      this.cancelGesture();
      this.remember();
      this.doc.effects = { ...NEUTRAL_EFFECTS };
      this.changed();
    });
    document
      .querySelector("#image-size-reset")
      .addEventListener("click", () => {
        this.cancelGesture();
        this.remember();
        const c = this.doc.crop,
          rotated = this.doc.transform.rotation % 180 !== 0;
        Object.assign(this.doc.transform, {
          width: rotated ? c.height : c.width,
          height: rotated ? c.width : c.height,
        });
        this.changed();
      });
    document
      .querySelector("#edit-blur-radius")
      .addEventListener("change", () => this.updateStyle());
  }
  syncImageControls() {
    const size = imageOutputSize(this.doc);
    document.querySelector("#image-width").value = size.width;
    document.querySelector("#image-height").value = size.height;
    for (const [key, value] of Object.entries(imageEffects(this.doc))) {
      document.querySelector(`#effect-${key}`).value = value;
      document.querySelector(`#effect-${key}-value`).textContent = `${value}%`;
    }
    document.querySelector("#image-rotation-value").textContent =
      `${this.doc.transform.rotation}°`;
    document
      .querySelector("#image-flip-x")
      .setAttribute("aria-pressed", String(this.doc.transform.flipX));
    document
      .querySelector("#image-flip-y")
      .setAttribute("aria-pressed", String(this.doc.transform.flipY));
  }
  bind() {
    document
      .querySelectorAll("[data-tool]")
      .forEach((button) =>
        button.addEventListener("click", () =>
          this.setTool(button.dataset.tool),
        ),
      );
    document
      .querySelector("#edit-undo")
      .addEventListener("click", () => this.undo());
    document
      .querySelector("#edit-redo")
      .addEventListener("click", () => this.redo());
    document
      .querySelector("#edit-delete")
      .addEventListener("click", () => this.deleteSelection());
    document
      .querySelector("#crop-apply")
      .addEventListener("click", () => this.applyCrop());
    document.querySelector("#crop-cancel").addEventListener("click", () => {
      this.pendingCrop = null;
      this.render();
    });
    document.querySelector("#editor-zoom").addEventListener("change", (e) => {
      this.zoom = e.target.value;
      this.layout();
    });
    for (const id of ["edit-color", "edit-stroke", "edit-font", "edit-text"])
      document
        .querySelector(`#${id}`)
        .addEventListener("change", () => this.updateStyle());
    this.overlay.addEventListener("pointerdown", (e) => this.down(e));
    this.overlay.addEventListener("pointermove", (e) => this.move(e));
    this.overlay.addEventListener("pointerup", (e) => this.up(e));
    this.overlay.addEventListener("pointercancel", (event) => {
      if (event.pointerId === this.pointerId) this.cancelGesture();
    });
    this.overlay.addEventListener("lostpointercapture", (event) => {
      if (
        event.pointerId === this.pointerId &&
        !this.overlay.hasPointerCapture(event.pointerId)
      )
        this.cancelGesture();
    });
    document.addEventListener("keydown", (e) => {
      if (e.target.closest("input,textarea,select,[contenteditable]")) return;
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "z") {
        e.preventDefault();
        e.shiftKey ? this.redo() : this.undo();
      } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "y") {
        e.preventDefault();
        this.redo();
      } else if (e.key === "Delete" || e.key === "Backspace") {
        if (this.selected) {
          e.preventDefault();
          this.deleteSelection();
        }
      } else if (e.key === "Escape") {
        this.pendingCrop = null;
        this.selected = null;
        this.setTool("select");
      } else if (!e.ctrlKey && !e.metaKey && !e.altKey) {
        const tools = {
          v: "select",
          c: "crop",
          a: "arrow",
          r: "rectangle",
          e: "ellipse",
          p: "pen",
          t: "text",
          b: "redact",
          u: "blur",
        };
        if (tools[e.key.toLowerCase()])
          this.setTool(tools[e.key.toLowerCase()]);
      }
    });
  }
  cancelGesture() {
    const drag = this.drag;
    this.drag = null;
    if (drag?.before) restoreSnapshot(this.doc, drag.before);
    this.releasePointer();
    if (drag) this.render();
  }
  releasePointer() {
    const pointerId = this.pointerId;
    this.pointerId = null;
    if (pointerId != null && this.overlay.hasPointerCapture(pointerId))
      this.overlay.releasePointerCapture(pointerId);
  }
  setTool(tool) {
    this.cancelGesture();
    this.tool = tool;
    this.pendingCrop = null;
    this.render();
    if (tool === "text") document.querySelector("#edit-text").focus();
  }
  style() {
    return {
      color: document.querySelector("#edit-color").value,
      stroke: Math.max(
        0.1,
        Math.min(
          200,
          Number(document.querySelector("#edit-stroke").value) || 4,
        ),
      ),
      fontSize: Math.max(
        1,
        Math.min(
          1000,
          Number(document.querySelector("#edit-font").value) || 32,
        ),
      ),
      text: document.querySelector("#edit-text").value.trim() || "Text",
      radius: Math.max(
        1,
        Math.min(
          40,
          Number(document.querySelector("#edit-blur-radius").value) || 12,
        ),
      ),
    };
  }
  updateStyle() {
    this.cancelGesture();
    const item = this.doc.objects.find((s) => s.id === this.selected);
    if (!item) return;
    this.remember();
    Object.assign(item, this.style());
    if (item.type === "text") this.measureText(item);
    this.changed();
  }
  measureText(s) {
    const ctx = document.createElement("canvas").getContext("2d");
    ctx.font = `${s.fontSize}px system-ui, Segoe UI, sans-serif`;
    s.width = Math.max(
      1,
      ...s.text.split("\n").map((line) => ctx.measureText(line).width),
    );
    s.height = s.text.split("\n").length * s.fontSize * 1.25;
  }
  point(e) {
    const rect = this.preview.getBoundingClientRect(),
      c = this.doc.crop;
    const point = this.viewMatrix
      .inverse()
      .transformPoint(
        new DOMPoint(e.clientX - rect.left, e.clientY - rect.top),
      );
    return {
      x: Math.max(c.x, Math.min(c.x + c.width, c.x + point.x)),
      y: Math.max(c.y, Math.min(c.y + c.height, c.y + point.y)),
    };
  }
  down(e) {
    if (e.button !== 0 || e.isPrimary === false) return;
    this.cancelGesture();
    e.preventDefault();
    this.overlay.focus();
    this.pointerId = e.pointerId;
    this.overlay.setPointerCapture(e.pointerId);
    const p = this.point(e),
      before = snapshot(this.doc);
    if (this.tool === "select") {
      const tolerance = 8 / this.scale;
      this.selected =
        [
          ...this.doc.objects.filter((s) => s.type === "blur"),
          ...this.doc.objects.filter((s) => s.type !== "blur"),
        ]
          .reverse()
          .find((s) => {
            const b = bounds(s);
            return (
              p.x >= b.x - tolerance &&
              p.x <= b.x + b.width + tolerance &&
              p.y >= b.y - tolerance &&
              p.y <= b.y + b.height + tolerance
            );
          })?.id || null;
      const original = this.doc.objects.find((s) => s.id === this.selected);
      this.drag = original
        ? { start: p, before, original: clone(original), moving: true }
        : null;
      this.showSelectedStyle();
    } else if (this.tool === "text") {
      this.remember();
      const shape = {
        id: crypto.randomUUID(),
        type: "text",
        ...p,
        ...this.style(),
        width: 1,
        height: 1,
      };
      this.measureText(shape);
      this.doc.objects.push(shape);
      this.selected = shape.id;
      this.changed();
      this.tool = "select";
    } else
      this.drag = {
        start: p,
        before,
        shape: {
          id: crypto.randomUUID(),
          type: this.tool,
          ...p,
          width: 0,
          height: 0,
          ...this.style(),
          ...(this.tool === "pen" ? { points: [p] } : {}),
        },
      };
    this.render();
  }
  move(e) {
    const d = this.drag;
    if (!d || e.pointerId !== this.pointerId) return;
    const p = this.point(e);
    if (d.moving) {
      const item = this.doc.objects.find((s) => s.id === this.selected);
      if (!item) {
        this.cancelGesture();
        return;
      }
      item.x = d.original.x + p.x - d.start.x;
      item.y = d.original.y + p.y - d.start.y;
      if (item.type === "pen")
        item.points = d.original.points.map((q) => ({
          x: q.x + p.x - d.start.x,
          y: q.y + p.y - d.start.y,
        }));
    } else {
      d.shape.width = p.x - d.start.x;
      d.shape.height = p.y - d.start.y;
      if (d.shape.type === "pen" && d.shape.points.length < 4000)
        d.shape.points.push(p);
    }
    this.renderOverlay();
  }
  up(e) {
    if (e.pointerId !== this.pointerId) return;
    const d = this.drag;
    if (!d) {
      this.releasePointer();
      return;
    }
    this.move(e);
    if (this.drag !== d) return;
    this.drag = null;
    this.releasePointer();
    if (d.moving) {
      if (JSON.stringify(snapshot(this.doc)) !== JSON.stringify(d.before)) {
        this.doc.past.push(d.before);
        this.doc.past = this.doc.past.slice(-60);
        this.doc.future = [];
        this.changed();
      }
    } else if (d.shape.type === "crop") {
      const b = bounds(d.shape);
      if (b.width >= 2 && b.height >= 2) this.pendingCrop = b;
    } else if (
      d.shape.type === "pen"
        ? d.shape.points.length > 1
        : Math.hypot(d.shape.width, d.shape.height) > 2
    ) {
      this.remember();
      this.doc.objects.push(d.shape);
      this.selected = d.shape.id;
      this.changed();
    }
    this.render();
  }
  remember() {
    this.doc.past.push(snapshot(this.doc));
    this.doc.past = this.doc.past.slice(-60);
    this.doc.future = [];
  }
  undo() {
    this.cancelGesture();
    if (!this.doc.past.length) return;
    this.doc.future.push(snapshot(this.doc));
    restoreSnapshot(this.doc, this.doc.past.pop());
    this.selected = null;
    this.pendingCrop = null;
    this.changed();
  }
  redo() {
    this.cancelGesture();
    if (!this.doc.future.length) return;
    this.doc.past.push(snapshot(this.doc));
    restoreSnapshot(this.doc, this.doc.future.pop());
    this.selected = null;
    this.pendingCrop = null;
    this.changed();
  }
  deleteSelection() {
    this.cancelGesture();
    if (!this.selected) return;
    this.remember();
    this.doc.objects = this.doc.objects.filter((s) => s.id !== this.selected);
    this.selected = null;
    this.changed();
  }
  applyCrop() {
    this.cancelGesture();
    if (!this.pendingCrop) return;
    const b = this.pendingCrop;
    this.remember();
    this.doc.crop = {
      x: Math.ceil(b.x),
      y: Math.ceil(b.y),
      width: Math.max(1, Math.floor(b.x + b.width) - Math.ceil(b.x)),
      height: Math.max(1, Math.floor(b.y + b.height) - Math.ceil(b.y)),
    };
    const rotated = this.doc.transform.rotation % 180 !== 0;
    this.doc.transform.width = rotated
      ? this.doc.crop.height
      : this.doc.crop.width;
    this.doc.transform.height = rotated
      ? this.doc.crop.width
      : this.doc.crop.height;
    this.pendingCrop = null;
    this.selected = null;
    this.changed();
  }
  showSelectedStyle() {
    const s = this.doc.objects.find((s) => s.id === this.selected);
    if (!s) return;
    document.querySelector("#edit-color").value = s.color;
    document.querySelector("#edit-stroke").value = s.stroke;
    if (s.fontSize) document.querySelector("#edit-font").value = s.fontSize;
    if (s.type === "text") document.querySelector("#edit-text").value = s.text;
    if (s.type === "blur")
      document.querySelector("#edit-blur-radius").value = s.radius;
  }
  changed() {
    this.render();
    const saved = clone(this.doc),
      revision = ++this.saveRevision;
    document.querySelector("#edit-save-state").textContent = "Saving…";
    this.saveChain = this.saveChain
      .catch(() => {})
      .then(() => this.persist(saved))
      .then(
        () => {
          this.persistedRevision = revision;
          if (revision === this.saveRevision)
            document.querySelector("#edit-save-state").textContent =
              "Saved locally";
        },
        (error) => {
          if (revision === this.saveRevision) {
            document.querySelector("#edit-save-state").textContent =
              "Not saved";
            this.notify(`Edits could not be saved: ${error.message}`);
          }
          throw error;
        },
      );
    this.saveChain.catch(() => {});
  }
  layout() {
    const c = this.doc.crop,
      size = imageOutputSize(this.doc);
    const zoom =
      this.zoom === "fit"
        ? Math.min(1, Math.max(120, this.stage.clientWidth - 56) / size.width)
        : Number(this.zoom);
    this.viewMatrix = new DOMMatrix()
      .scale(zoom)
      .multiply(outputMatrix(this.doc));
    this.scale = Math.max(
      0.000001,
      Math.min(
        Math.hypot(this.viewMatrix.a, this.viewMatrix.b),
        Math.hypot(this.viewMatrix.c, this.viewMatrix.d),
      ),
    );
    Object.assign(this.preview.style, {
      width: `${size.width * zoom}px`,
      height: `${size.height * zoom}px`,
      transform: "none",
    });
    Object.assign(this.content.style, {
      width: `${c.width}px`,
      height: `${c.height}px`,
      transform: this.viewMatrix.toString(),
    });
    Object.assign(this.source.style, {
      left: `${-c.x}px`,
      top: `${-c.y}px`,
      width: `${this.record.report.dimensionsPx.width}px`,
      height: `${this.record.report.dimensionsPx.height}px`,
      filter: effectFilter(this.doc),
    });
    this.overlay.setAttribute(
      "viewBox",
      `${c.x} ${c.y} ${c.width} ${c.height}`,
    );
    this.overlay.setAttribute("width", c.width);
    this.overlay.setAttribute("height", c.height);
    document.querySelector("#edit-dimensions").textContent =
      `${size.width} × ${size.height} px · ${Math.round(zoom * 100)}%`;
  }
  render() {
    this.layout();
    this.syncImageControls();
    this.renderOverlay();
    document
      .querySelectorAll("[data-tool]")
      .forEach((b) =>
        b.setAttribute("aria-pressed", String(b.dataset.tool === this.tool)),
      );
    this.overlay.dataset.tool = this.tool;
    document.querySelector("#edit-undo").disabled = !this.doc.past.length;
    document.querySelector("#edit-redo").disabled = !this.doc.future.length;
    document.querySelector("#edit-delete").disabled = !this.selected;
    document.querySelector("#crop-controls").hidden = !this.pendingCrop;
    document.querySelector("#text-controls").hidden =
      this.tool !== "text" &&
      this.doc.objects.find((s) => s.id === this.selected)?.type !== "text";
    document.querySelector("#blur-controls").hidden =
      this.tool !== "blur" &&
      this.doc.objects.find((s) => s.id === this.selected)?.type !== "blur";
    document.querySelector("#edit-help").textContent =
      this.tool === "blur"
        ? "Drag to soften image details. Blur is a visual effect, not secure redaction. Use Redact for private information."
        : this.tool === "redact"
          ? "Drag to cover private details. Image/PDF exports remove covered pixels; editable projects retain the original."
          : this.tool === "crop"
            ? "Drag a crop area, then choose Apply crop."
            : this.tool === "text"
              ? "Enter your text, then click on the image to place it."
              : this.tool === "select"
                ? "Select an annotation to move it, change its style, or delete it."
                : "Drag on the image to draw. Ctrl+Z to undo.";
    document.querySelector("#edited-pdf-note").hidden = !this.isEdited();
    document.querySelector("#redaction-project-note").hidden =
      !this.doc.objects.some((s) => s.type === "redact");
  }
  renderBlur(shape) {
    const b = bounds(shape),
      matrix = outputMatrix(this.doc),
      sx = Math.hypot(matrix.a, matrix.b),
      sy = Math.hypot(matrix.c, matrix.d),
      radius = shape.radius || 12;
    if (!b.width || !b.height) return;
    const key = `image-blur-${this.doc.objects.indexOf(shape) + 1}`,
      defs = svg("defs"),
      clip = svg("clipPath", { id: `${key}-clip` }),
      cropClip = svg("clipPath", { id: `${key}-crop` }),
      filter = svg("filter", {
        id: key,
        filterUnits: "userSpaceOnUse",
        x: b.x - (3 * radius) / sx,
        y: b.y - (3 * radius) / sy,
        width: b.width + (6 * radius) / sx,
        height: b.height + (6 * radius) / sy,
        "color-interpolation-filters": "sRGB",
      });
    clip.append(svg("rect", b));
    cropClip.append(svg("rect", this.doc.crop));
    filter.append(
      svg("feGaussianBlur", { stdDeviation: `${radius / sx} ${radius / sy}` }),
    );
    defs.append(clip, cropClip, filter);
    const group = svg("g", {
        "data-object-id": shape.id,
        "clip-path": `url(#${key}-clip)`,
      }),
      blur = svg("g", { filter: `url(#${key})` }),
      pixels = svg("g", { "clip-path": `url(#${key}-crop)` });
    for (let i = 0; i < this.tiles.length; i++) {
      const tile = this.tiles[i];
      if (
        tile.destXPx > b.x + b.width + (3 * radius) / sx ||
        tile.destXPx + tile.widthPx < b.x - (3 * radius) / sx ||
        tile.destYPx > b.y + b.height + (3 * radius) / sy ||
        tile.destYPx + tile.heightPx < b.y - (3 * radius) / sy
      )
        continue;
      const image = svg("image", {
        href: this.source.children[i].src,
        x: tile.destXPx,
        y: tile.destYPx,
        width: tile.widthPx,
        height: tile.heightPx,
      });
      pixels.append(image);
    }
    pixels.style.filter = effectFilter(this.doc);
    blur.append(pixels);
    group.append(blur);
    this.overlay.append(defs, group);
  }
  renderOverlay() {
    this.overlay.replaceChildren();
    const shapes = [
      ...this.doc.objects,
      ...(this.drag?.shape?.type === "blur" ? [this.drag.shape] : []),
    ];
    for (const shape of shapes.filter((s) => s.type === "blur"))
      this.renderBlur(shape);
    for (const shape of this.doc.objects.filter((s) => s.type !== "blur"))
      this.overlay.append(drawSvg(shape));
    const d = this.drag;
    if (d?.shape && d.shape.type !== "crop" && d.shape.type !== "blur")
      this.overlay.append(drawSvg(d.shape));
    const region =
      this.pendingCrop || (d?.shape?.type === "crop" ? bounds(d.shape) : null);
    if (region)
      this.overlay.append(
        svg("rect", {
          ...region,
          fill: "#208bff22",
          stroke: "#79b8ff",
          "stroke-width": 2 / this.scale,
          "stroke-dasharray": `${7 / this.scale} ${4 / this.scale}`,
        }),
      );
    const selected = this.doc.objects.find((s) => s.id === this.selected);
    if (selected) {
      const b = bounds(selected),
        pad = 5 / this.scale;
      this.overlay.append(
        svg("rect", {
          x: b.x - pad,
          y: b.y - pad,
          width: b.width + 2 * pad,
          height: b.height + 2 * pad,
          fill: "none",
          stroke: "#79b8ff",
          "stroke-width": 1.5 / this.scale,
          "stroke-dasharray": `${4 / this.scale} ${3 / this.scale}`,
        }),
      );
    }
  }
  isEdited() {
    const c = this.doc.crop,
      s = this.record.report.dimensionsPx;
    return (
      !!this.doc.objects.length ||
      Object.entries(imageEffects(this.doc)).some(
        ([key, value]) => value !== NEUTRAL_EFFECTS[key],
      ) ||
      this.doc.transform.rotation !== 0 ||
      this.doc.transform.flipX ||
      this.doc.transform.flipY ||
      this.doc.transform.width !== c.width ||
      this.doc.transform.height !== c.height ||
      c.x !== 0 ||
      c.y !== 0 ||
      c.width !== s.width ||
      c.height !== s.height
    );
  }
  async exportInput(onProgress = () => {}) {
    const document = clone(this.doc);
    await this.saveChain.catch(() => {});
    return renderEditedCapture(this.record, this.tiles, document, onProgress);
  }
  destroy() {
    removeEventListener("beforeunload", this.beforeUnload);
    this.resizeObserver.disconnect();
    this.urls.forEach((url) => URL.revokeObjectURL(url));
  }
}

// Resampling each source tile independently clamps its image edges. Stitch a
// native-resolution apron first, then sample only its integer output interior.
// Both patch size and final tiles stay bounded, including severe downscales.
const SOURCE_PATCH_WIDTH = 2048;
const SOURCE_PATCH_HEIGHT = 1024;
function inverseWindow(matrix, left, top, width, height) {
  const inverse = matrix.inverse(),
    points = [
      [left, top],
      [left + width, top],
      [left, top + height],
      [left + width, top + height],
    ].map(([x, y]) => inverse.transformPoint(new DOMPoint(x, y)));
  return {
    left: Math.min(...points.map((p) => p.x)),
    right: Math.max(...points.map((p) => p.x)),
    top: Math.min(...points.map((p) => p.y)),
    bottom: Math.max(...points.map((p) => p.y)),
  };
}
function intersects(tile, rect) {
  return (
    tile.destXPx < rect.right &&
    tile.destXPx + tile.widthPx > rect.left &&
    tile.destYPx < rect.bottom &&
    tile.destYPx + tile.heightPx > rect.top
  );
}
function tilesOverlap(tiles) {
  if (tiles.length < 2) return false;
  // Sweep the less crowded axis: horizontal strips and vertical columns both
  // keep a small active set, without comparing every pair of ordinary tiles.
  const width = Math.max(...tiles.map((t) => t.destXPx + t.widthPx)),
    height = Math.max(...tiles.map((t) => t.destYPx + t.heightPx)),
    horizontal =
      tiles.reduce((sum, t) => sum + t.widthPx, 0) / width <
      tiles.reduce((sum, t) => sum + t.heightPx, 0) / height,
    start = horizontal ? "destXPx" : "destYPx",
    length = horizontal ? "widthPx" : "heightPx",
    other = horizontal ? "destYPx" : "destXPx",
    otherLength = horizontal ? "heightPx" : "widthPx";
  let active = [];
  for (const tile of [...tiles].sort((a, b) => a[start] - b[start])) {
    active = active.filter((t) => t[start] + t[length] > tile[start]);
    if (
      active.some(
        (t) =>
          t[other] < tile[other] + tile[otherLength] &&
          t[other] + t[otherLength] > tile[other],
      )
    )
      return true;
    active.push(tile);
  }
  return false;
}
function unitPixelTransform(matrix) {
  return (
    [matrix.a, matrix.b, matrix.c, matrix.d].every((value) =>
      [-1, 0, 1].includes(value),
    ) &&
    Number.isInteger(matrix.e) &&
    Number.isInteger(matrix.f) &&
    Math.hypot(matrix.a, matrix.b) === 1 &&
    Math.hypot(matrix.c, matrix.d) === 1
  );
}
async function sourcePatch(tiles, rect) {
  const canvas = new OffscreenCanvas(
      rect.right - rect.left,
      rect.bottom - rect.top,
    ),
    context = canvas.getContext("2d", { alpha: true });
  for (const tile of tiles) {
    if (!intersects(tile, rect)) continue;
    const left = Math.max(rect.left, tile.destXPx),
      top = Math.max(rect.top, tile.destYPx),
      right = Math.min(rect.right, tile.destXPx + tile.widthPx),
      bottom = Math.min(rect.bottom, tile.destYPx + tile.heightPx);
    const bitmap = await createImageBitmap(
      tile.blob,
      left - tile.destXPx,
      top - tile.destYPx,
      right - left,
      bottom - top,
    );
    try {
      context.drawImage(bitmap, left - rect.left, top - rect.top);
    } finally {
      bitmap.close();
    }
  }
  return canvas;
}
async function paintRasterWindow(
  context,
  tiles,
  crop,
  matrix,
  window,
  filter = "none",
  compositeEffects = false,
) {
  const transform = new DOMMatrix()
    .translate(-window.left, -window.top)
    .multiply(matrix);
  // No apron or intermediate decodes are needed for ordinary annotations,
  // integer crops, quarter-turns and flips that preserve the source resolution.
  if (unitPixelTransform(matrix) && !compositeEffects) {
    const rect = inverseWindow(
      matrix,
      window.left,
      window.top,
      window.width,
      window.height,
    );
    context.save();
    context.setTransform(transform);
    context.beginPath();
    context.rect(crop.x, crop.y, crop.width, crop.height);
    context.clip();
    context.filter = filter;
    for (const tile of tiles) {
      if (!intersects(tile, rect)) continue;
      const bitmap = await createImageBitmap(tile.blob);
      try {
        context.drawImage(
          bitmap,
          tile.destXPx,
          tile.destYPx,
          tile.widthPx,
          tile.heightPx,
        );
      } finally {
        bitmap.close();
      }
    }
    context.restore();
    return;
  }
  const sx = Math.hypot(matrix.a, matrix.b),
    sy = Math.hypot(matrix.c, matrix.d),
    rotated = Math.abs(matrix.b) > Math.abs(matrix.a),
    ax = Math.ceil(4 / sx) + 2,
    ay = Math.ceil(4 / sy) + 2;
  const patchWidth = Math.max(
      1,
      Math.floor(
        (rotated
          ? SOURCE_PATCH_HEIGHT - 2 * ay - 4
          : SOURCE_PATCH_WIDTH - 2 * ax - 4) * (rotated ? sy : sx),
      ),
    ),
    patchHeight = Math.max(
      1,
      Math.floor(
        (rotated
          ? SOURCE_PATCH_WIDTH - 2 * ax - 4
          : SOURCE_PATCH_HEIGHT - 2 * ay - 4) * (rotated ? sx : sy),
      ),
    );
  for (let y = window.top; y < window.top + window.height; y += patchHeight)
    for (let x = window.left; x < window.left + window.width; x += patchWidth) {
      const width = Math.min(patchWidth, window.left + window.width - x),
        height = Math.min(patchHeight, window.top + window.height - y),
        needed = inverseWindow(matrix, x, y, width, height);
      const rect = {
        left: Math.max(crop.x, Math.floor(needed.left) - ax),
        top: Math.max(crop.y, Math.floor(needed.top) - ay),
        right: Math.min(crop.x + crop.width, Math.ceil(needed.right) + ax),
        bottom: Math.min(crop.y + crop.height, Math.ceil(needed.bottom) + ay),
      };
      if (rect.right <= rect.left || rect.bottom <= rect.top) continue;
      if (
        rect.right - rect.left > SOURCE_PATCH_WIDTH ||
        rect.bottom - rect.top > SOURCE_PATCH_HEIGHT
      )
        throw new Error(
          "The image could not be resized within the bounded source patch. Choose a larger output size.",
        );
      const patch = await sourcePatch(tiles, rect);
      context.save();
      context.resetTransform();
      context.beginPath();
      context.rect(x - window.left, y - window.top, width, height);
      context.clip();
      context.setTransform(transform);
      context.beginPath();
      context.rect(crop.x, crop.y, crop.width, crop.height);
      context.clip();
      context.filter = filter;
      context.imageSmoothingQuality = "high";
      context.drawImage(patch, rect.left, rect.top);
      context.restore();
    }
}
async function prepareResamplingRaster(tiles, crop, matrix) {
  let raster = tiles,
    region = { ...crop },
    map = matrix;
  // Keep the final sampling footprint bounded even when a very tall capture is
  // reduced to a handful of pixels. Only axes needing reduction are processed.
  while (
    Math.hypot(map.a, map.b) < 1 / 16 ||
    Math.hypot(map.c, map.d) < 1 / 16
  ) {
    const width =
        Math.hypot(map.a, map.b) < 1 / 16
          ? Math.max(1, Math.ceil(region.width / 8))
          : region.width,
      height =
        Math.hypot(map.c, map.d) < 1 / 16
          ? Math.max(1, Math.ceil(region.height / 8))
          : region.height;
    const reduction = new DOMMatrix()
        .scale(width / region.width, height / region.height)
        .translate(-region.x, -region.y),
      next = [];
    for (let top = 0; top < height; top += 1024)
      for (let left = 0; left < width; left += 2048) {
        const w = Math.min(2048, width - left),
          h = Math.min(1024, height - top),
          canvas = new OffscreenCanvas(w, h);
        await paintRasterWindow(
          canvas.getContext("2d", { alpha: true }),
          raster,
          region,
          reduction,
          { left, top, width: w, height: h },
        );
        next.push({
          index: next.length,
          destXPx: left,
          destYPx: top,
          widthPx: w,
          heightPx: h,
          blob: await canvas.convertToBlob({ type: "image/png" }),
        });
      }
    raster = next;
    region = { x: 0, y: 0, width, height };
    map = matrix.multiply(
      new DOMMatrix()
        .translate(crop.x, crop.y)
        .scale(crop.width / width, crop.height / height),
    );
  }
  return { tiles: raster, crop: region, matrix: map };
}

export async function renderEditedCapture(
  record,
  tiles,
  document,
  onProgress = () => {},
) {
  const c = document.crop,
    size = record.report.dimensionsPx,
    out = imageOutputSize(document),
    t = imageTransform(document),
    effects = imageEffects(document);
  const neutral = Object.entries(effects).every(
    ([key, value]) => value === NEUTRAL_EFFECTS[key],
  );
  if (
    !document.objects.length &&
    c.x === 0 &&
    c.y === 0 &&
    c.width === size.width &&
    c.height === size.height &&
    t.rotation === 0 &&
    !t.flipX &&
    !t.flipY &&
    out.width === size.width &&
    out.height === size.height &&
    neutral
  )
    return { record, tiles };
  if (
    !Number.isInteger(out.width) ||
    !Number.isInteger(out.height) ||
    out.width < 1 ||
    out.height < 1 ||
    out.width > 200000 ||
    out.height > 200000 ||
    out.width * out.height > 100000000
  )
    throw new Error(
      "The edited image exceeds the 100 million pixel export limit. Choose a smaller output size.",
    );
  const output = [],
    columns = Math.ceil(out.width / 2048),
    rows = Math.ceil(out.height / 1024),
    total = columns * rows;
  const matrix = outputMatrix(document).translate(-c.x, -c.y);
  const raster = await prepareResamplingRaster(
    [...tiles].sort((a, b) => a.index - b.index),
    c,
    matrix,
  );
  // Pointwise color adjustments apply to the composed source, including alpha
  // overlaps in imported projects, just as they do in the grouped preview.
  const filter = neutral ? "none" : effectFilter(document),
    compositeEffects = !neutral && tilesOverlap(raster.tiles);
  const blurs = document.objects.filter((s) => s.type === "blur"),
    pad = blurs.length
      ? Math.ceil(3 * Math.max(...blurs.map((s) => s.radius || 12)))
      : 0;
  for (let top = 0; top < out.height; top += 1024)
    for (let left = 0; left < out.width; left += 2048) {
      const width = Math.min(2048, out.width - left),
        height = Math.min(1024, out.height - top),
        pw = width + 2 * pad,
        ph = height + 2 * pad;
      const source = new OffscreenCanvas(pw, ph),
        ctx = source.getContext("2d", { alpha: true });
      await paintRasterWindow(
        ctx,
        raster.tiles,
        raster.crop,
        raster.matrix,
        { left: left - pad, top: top - pad, width: pw, height: ph },
        filter,
        compositeEffects,
      );
      const canvas = new OffscreenCanvas(width, height),
        outputContext = canvas.getContext("2d", { alpha: true });
      outputContext.drawImage(source, -pad, -pad);
      for (const shape of blurs) {
        const b = bounds(shape);
        outputContext.save();
        outputContext.setTransform(
          new DOMMatrix().translate(-left, -top).multiply(matrix),
        );
        outputContext.beginPath();
        outputContext.rect(b.x, b.y, b.width, b.height);
        outputContext.clip();
        outputContext.resetTransform();
        outputContext.filter = `blur(${shape.radius || 12}px)`;
        outputContext.drawImage(source, -pad, -pad);
        outputContext.restore();
      }
      outputContext.setTransform(
        new DOMMatrix().translate(-left, -top).multiply(matrix),
      );
      for (const shape of document.objects)
        if (shape.type !== "blur") drawCanvas(outputContext, shape);
      output.push({
        captureId: record.id,
        index: output.length,
        destXPx: left,
        destYPx: top,
        widthPx: width,
        heightPx: height,
        blob: await canvas.convertToBlob({ type: "image/png" }),
      });
      onProgress(output.length / total);
    }
  const edited = clone(record);
  edited.report.dimensionsPx = out;
  const ratio = size.width / (record.report.dimensionsCss?.width || size.width);
  edited.report.dimensionsCss = {
    width: out.width / ratio,
    height: out.height / ratio,
  };
  edited.report.tileCount = output.length;
  delete edited.semantics;
  return { record: edited, tiles: output };
}
