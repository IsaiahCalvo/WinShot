import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function withImage(check) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-formats-"));
  const browser = await launchCaptureBrowser(path.join(directory, "profile"));
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const page = await browser.newPage(), errors = [];
    page.on("pageerror", e => errors.push(e.message));
    const library = `chrome-extension://${id}/src/library.html`;
    await page.goto(library);
    const bytes = await page.evaluate(async () => {
      const canvas = new OffscreenCanvas(33, 21), ctx = canvas.getContext("2d");
      ctx.fillStyle = "#2090e0"; ctx.fillRect(0, 0, 33, 21);
      ctx.fillStyle = "#f0b020"; ctx.fillRect(16, 0, 17, 21);
      ctx.clearRect(29, 17, 4, 4);
      return [...new Uint8Array(await (await canvas.convertToBlob({ type: "image/png" })).arrayBuffer())];
    });
    const original = path.join(directory, "source.png"); fs.writeFileSync(original, Buffer.from(bytes));
    await (await page.$("#open-file")).uploadFile(original);
    await page.waitForSelector("#preview img");
    await page.evaluate(async () => {
      const store = await import("./store.js"), record = await store.getRecord(new URL(location.href).searchParams.get("id"));
      record.editorDocument.objects.push({ id: "private", type: "redact", x: 2, y: 2, width: 8, height: 8, color: "#111111", stroke: 4 });
      await store.updateRecord(record);
    });
    await page.reload(); await page.waitForSelector("#preview img");
    const downloads = path.join(directory, "downloads"); fs.mkdirSync(downloads);
    await (await page.createCDPSession()).send("Page.setDownloadBehavior", { behavior: "allow", downloadPath: downloads });
    async function save(selector, suffix) {
      await page.click(selector);
      try { await expect.poll(() => fs.readdirSync(downloads).find(n => n.endsWith(suffix)), { timeout: 15000 }).toBeTruthy(); }
      catch(error) { throw new Error(`${error.message}; download files:${JSON.stringify(fs.readdirSync(downloads))}; UI:${await page.$eval("#toast",n=>n.textContent)}`); }
      const name = fs.readdirSync(downloads).find(n => n.endsWith(suffix));
      return path.join(downloads, name);
    }
    await check({ page, library, save, directory, original, bytes });
    expect(errors).toEqual([]);
  } finally {
    await browser.close(); fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}

async function pixels(page, filename, type) {
  return await page.evaluate(async ({ bytes, type }) => {
    const bitmap = await createImageBitmap(new Blob([new Uint8Array(bytes)], { type }));
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height), ctx = canvas.getContext("2d"); ctx.drawImage(bitmap, 0, 0); bitmap.close();
    return { width: canvas.width, height: canvas.height, redacted: [...ctx.getImageData(5, 5, 1, 1).data], blue: [...ctx.getImageData(12, 12, 1, 1).data], yellow: [...ctx.getImageData(20, 12, 1, 1).data], matte: [...ctx.getImageData(31, 19, 1, 1).data] };
  }, { bytes: [...fs.readFileSync(filename)], type });
}

test("BMP and GIF UI exports decode edited pixels and reopen through the real image importer", async () => {
  await withImage(async ({ page, library, save, directory }) => {
    const editorUrl = page.url();
    for (const [kind, type] of [["bmp", "image/bmp"], ["gif", "image/gif"]]) {
      await page.goto(editorUrl); await page.waitForSelector("#preview img");
      const filename = await save(`#save-${kind}`, `.${kind}`), bytes = fs.readFileSync(filename);
      expect(bytes.subarray(0, kind === "bmp" ? 2 : 6).toString()).toBe(kind === "bmp" ? "BM" : "GIF89a");
      expect(await pixels(page, filename, type)).toEqual({ width: 33, height: 21, redacted: [17,17,17,255], blue: [32,144,224,255], yellow: [240,176,32,255], matte: [255,255,255,255] });
      await page.goto(library); await (await page.$("#open-file")).uploadFile(filename); await page.waitForSelector("#preview img");
      expect(await page.evaluate(async () => {
        const { getRecord, getTiles } = await import("./store.js"), id = new URL(location.href).searchParams.get("id"), record = await getRecord(id), tiles = await getTiles(id);
        const bitmap = await createImageBitmap(tiles[0].blob), c = new OffscreenCanvas(33,21), ctx=c.getContext("2d");ctx.drawImage(bitmap,0,0);bitmap.close();
        return { dimensions:record.report.dimensionsPx, objects:record.editorDocument.objects.length, redacted:[...ctx.getImageData(5,5,1,1).data] };
      })).toEqual({ dimensions:{width:33,height:21}, objects:0, redacted:[17,17,17,255] });
    }
  });
});

test("editable PNG keeps a flattened standard preview and reopens original pixels and safe edit schema", async () => {
  await withImage(async ({ page, library, save, directory }) => {
    await page.evaluate(async () => {
      const s=await import("./store.js"),r=await s.getRecord(new URL(location.href).searchParams.get("id"));
      r.editorDocument.transform={rotation:0,flipX:false,flipY:false,width:33,height:21};
      r.editorDocument.effects={brightness:100,contrast:100,saturation:100,grayscale:0,sepia:0};await s.updateRecord(r);
    });
    await page.reload(); await page.waitForSelector("#preview img");
    const editable = await save("#save-editable-png", ".editable.png");
    expect((await pixels(page, editable, "image/png")).redacted).toEqual([17,17,17,255]);
    const embedded = await page.evaluate(async bytes => {
      const {readPngProject}=await import("./png-project.js");return JSON.parse(await readPngProject(new Blob([new Uint8Array(bytes)])));
    }, [...fs.readFileSync(editable)]);
    expect(embedded.format).toBe("winshot-browser-project");expect(embedded.editorDocument.objects).toHaveLength(1);
    expect(embedded.editorDocument.transform).not.toHaveProperty("untrusted");expect(embedded.editorDocument.effects).not.toHaveProperty("filter");
    const normal = await save("#save-png", "source.png");
    expect((await pixels(page, normal, "image/png")).redacted).toEqual([17,17,17,255]);
    const normalBytes=fs.readFileSync(normal),chunkTypes=[];
    for(let at=8;at<normalBytes.length;at+=normalBytes.readUInt32BE(at)+12)chunkTypes.push(normalBytes.toString("ascii",at+4,at+8));
    expect(chunkTypes.every(type=>["IHDR","IDAT","IEND"].includes(type))).toBe(true);
    expect(await page.evaluate(async bytes => (await import("./png-project.js")).readPngProject(new Blob([new Uint8Array(bytes)])), [...fs.readFileSync(normal)])).toBeNull();
    // Exercise untrusted fields through the actual import boundary, rather than
    // inserting invalid records directly into the editor's private database.
    embedded.editorDocument.transform.untrusted="discard";embedded.editorDocument.effects.filter="url(https://example.com/ignored)";
    const untrustedBytes=await page.evaluate(async({png,project})=>[...new Uint8Array(await(await(await import("./png-project.js")).embedPngProject(new Blob([new Uint8Array(png)]),new Blob([JSON.stringify(project)]))).arrayBuffer())],{png:[...fs.readFileSync(normal)],project:embedded});
    const untrusted=path.join(directory,"unknown-fields.editable.png");fs.writeFileSync(untrusted,Buffer.from(untrustedBytes));
    await page.goto(library);await (await page.$("#open-file")).uploadFile(untrusted);await page.waitForSelector("#preview img");
    expect(await page.evaluate(async () => {
      const s=await import("./store.js"),id=new URL(location.href).searchParams.get("id"),r=await s.getRecord(id),t=await s.getTiles(id),b=await createImageBitmap(t[0].blob),c=new OffscreenCanvas(33,21),ctx=c.getContext("2d");ctx.drawImage(b,0,0);b.close();
      return {objects:r.editorDocument.objects.length,original:[...ctx.getImageData(5,5,1,1).data],transform:r.editorDocument.transform,effects:r.editorDocument.effects,unknownFields:"untrusted"in r.editorDocument.transform||"filter"in r.editorDocument.effects};
    })).toMatchObject({objects:1,original:[32,144,224,255],transform:{width:33,height:21,rotation:0},effects:{brightness:100},unknownFields:false});
  });
});

test("editable PNG rejects damaged checksums, duplicate metadata and oversize/truncated chunks before storage", async () => {
  await withImage(async ({ page, library, save, directory, bytes }) => {
    const editable = await save("#save-editable-png", ".editable.png"), original=fs.readFileSync(editable);
    const invalidSchema=await page.evaluate(async({png,project})=>{
      const {readPngProject,embedPngProject}=await import("./png-project.js"),data=JSON.parse(await readPngProject(new Blob([new Uint8Array(project)]))),out=[];
      for(const kind of ["rotation","radius","effect"]){
        const copy=structuredClone(data);
        if(kind==="rotation")copy.editorDocument.transform={rotation:45,flipX:false,flipY:false,width:33,height:21};
        if(kind==="radius")Object.assign(copy.editorDocument.objects[0],{type:"blur",radius:41});
        if(kind==="effect")copy.editorDocument.effects={brightness:201,contrast:100,saturation:100,grayscale:0,sepia:0};
        out.push([kind,[...new Uint8Array(await(await embedPngProject(new Blob([new Uint8Array(png)]),new Blob([JSON.stringify(copy)]))).arrayBuffer())]]);
      }return out;
    },{png:bytes,project:[...original]});
    let start=8;while(original.toString("ascii",start+4,start+8)!=="wsPJ")start+=original.readUInt32BE(start)+12;
    const chunkEnd=start+original.readUInt32BE(start)+12;
    const damaged=Buffer.from(original);damaged[start+8]^=1;
    const truncated=Buffer.from(original);truncated.writeUInt32BE(0x7fffffff,start);
    const duplicate=Buffer.concat([original.subarray(0,chunkEnd),original.subarray(start,chunkEnd),original.subarray(chunkEnd)]);
    await page.goto(library);
    const count=await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length);
    for(const [name,bytes]of[["checksum",damaged],["truncated",truncated],["duplicate",duplicate],...invalidSchema.map(([name,data])=>[name,Buffer.from(data)])]){
      const filename=path.join(directory,`${name}.png`);fs.writeFileSync(filename,bytes);await(await page.$("#open-file")).uploadFile(filename);
      await page.waitForFunction(()=>document.querySelector("#status").classList.contains("error"));
      expect(await page.$eval("#status",n=>n.textContent)).toMatch(/checksum|truncated|duplicate|transformation|coordinates/i);
      expect(await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length)).toBe(count);
    }
  });
});
