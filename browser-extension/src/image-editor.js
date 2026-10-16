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
const snapshot = (doc) => clone({ crop: doc.crop, objects: doc.objects });
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
        ...(s.type === "redact" ? { fill: "#111111", stroke: "none" } : {}),
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
    ctx.fillStyle = "#111111";
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
    this.tiles = tiles;
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
    for (const tile of [...tiles].sort((a, b) => a.index - b.index)) {
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
    this.render();
    this.resizeObserver = new ResizeObserver(() => this.layout());
    this.resizeObserver.observe(stage);
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
        };
        if (tools[e.key.toLowerCase()])
          this.setTool(tools[e.key.toLowerCase()]);
      }
    });
  }
  cancelGesture() {
    const drag = this.drag;
    this.drag = null;
    if (drag?.before) Object.assign(this.doc, drag.before);
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
    const rect = this.overlay.getBoundingClientRect(),
      c = this.doc.crop;
    return {
      x: Math.max(
        c.x,
        Math.min(c.x + c.width, c.x + (e.clientX - rect.left) / this.scale),
      ),
      y: Math.max(
        c.y,
        Math.min(c.y + c.height, c.y + (e.clientY - rect.top) / this.scale),
      ),
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
        [...this.doc.objects].reverse().find((s) => {
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
    Object.assign(this.doc, this.doc.past.pop());
    this.selected = null;
    this.pendingCrop = null;
    this.changed();
  }
  redo() {
    this.cancelGesture();
    if (!this.doc.future.length) return;
    this.doc.past.push(snapshot(this.doc));
    Object.assign(this.doc, this.doc.future.pop());
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
    const c = this.doc.crop;
    this.scale =
      this.zoom === "fit"
        ? Math.min(1, Math.max(120, this.stage.clientWidth - 56) / c.width)
        : Number(this.zoom);
    Object.assign(this.preview.style, {
      width: `${c.width * this.scale}px`,
      height: `${c.height * this.scale}px`,
      transform: "none",
    });
    Object.assign(this.content.style, {
      width: `${c.width}px`,
      height: `${c.height}px`,
      transform: `scale(${this.scale})`,
    });
    Object.assign(this.source.style, { left: `${-c.x}px`, top: `${-c.y}px` });
    this.overlay.setAttribute(
      "viewBox",
      `${c.x} ${c.y} ${c.width} ${c.height}`,
    );
    this.overlay.setAttribute("width", c.width);
    this.overlay.setAttribute("height", c.height);
    document.querySelector("#edit-dimensions").textContent =
      `${c.width} × ${c.height} px · ${Math.round(this.scale * 100)}%`;
  }
  render() {
    this.layout();
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
    document.querySelector("#edit-help").textContent =
      this.tool === "redact"
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
  renderOverlay() {
    this.overlay.replaceChildren();
    for (const shape of this.doc.objects) this.overlay.append(drawSvg(shape));
    const d = this.drag;
    if (d?.shape && d.shape.type !== "crop")
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

export async function renderEditedCapture(
  record,
  tiles,
  document,
  onProgress = () => {},
) {
  const c = document.crop,
    size = record.report.dimensionsPx;
  if (
    !document.objects.length &&
    c.x === 0 &&
    c.y === 0 &&
    c.width === size.width &&
    c.height === size.height
  )
    return { record, tiles };
  const output = [];
  const columns = Math.ceil(c.width / 2048),
    rows = Math.ceil(c.height / 1024),
    total = columns * rows;
  // At most one destination tile and its intersecting source bitmaps live at a
  // time, even when the capture exceeds the browser's single-canvas limit.
  for (let top = 0; top < c.height; top += 1024)
    for (let left = 0; left < c.width; left += 2048) {
      const width = Math.min(2048, c.width - left),
        height = Math.min(1024, c.height - top),
        x = c.x + left,
        y = c.y + top;
      const canvas = new OffscreenCanvas(width, height),
        ctx = canvas.getContext("2d", { alpha: true });
      for (const tile of [...tiles].sort((a, b) => a.index - b.index)) {
        if (
          tile.destXPx >= x + width ||
          tile.destXPx + tile.widthPx <= x ||
          tile.destYPx >= y + height ||
          tile.destYPx + tile.heightPx <= y
        )
          continue;
        const bitmap = await createImageBitmap(tile.blob);
        try {
          ctx.drawImage(
            bitmap,
            tile.destXPx - x,
            tile.destYPx - y,
            tile.widthPx,
            tile.heightPx,
          );
        } finally {
          bitmap.close();
        }
      }
      ctx.save();
      ctx.translate(-x, -y);
      for (const shape of document.objects) drawCanvas(ctx, shape);
      ctx.restore();
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
  edited.report.dimensionsPx = { width: c.width, height: c.height };
  const ratio = size.width / (record.report.dimensionsCss?.width || size.width);
  edited.report.dimensionsCss = {
    width: c.width / ratio,
    height: c.height / ratio,
  };
  edited.report.tileCount = output.length;
  // Cropping/redaction would otherwise leave selectable text and live links at
  // stale coordinates, potentially exposing hidden content in the PDF.
  delete edited.semantics;
  return { record: edited, tiles: output };
}
