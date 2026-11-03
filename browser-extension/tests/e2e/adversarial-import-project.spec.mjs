import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function runLibrary(check) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-import-e2e-"));
  const browser = await launchCaptureBrowser(path.join(directory, "profile"));
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const page = await browser.newPage(), errors = [];
    page.on("pageerror", e => errors.push(e.message));
    await page.goto(`chrome-extension://${id}/src/library.html`);
    await page.waitForSelector("#open-file");
    const bytes = await page.evaluate(async () => {
      const canvas = new OffscreenCanvas(640, 360), ctx = canvas.getContext("2d");
      ctx.fillStyle = "#2090e0"; ctx.fillRect(0, 0, 640, 360);
      ctx.fillStyle = "#f0b020"; ctx.fillRect(320, 0, 320, 360);
      return [...new Uint8Array(await (await canvas.convertToBlob({type:"image/png"})).arrayBuffer())];
    });
    const image = path.join(directory, "my-image.png"); fs.writeFileSync(image, Buffer.from(bytes));
    await check({ browser, page, id, directory, image, bytes });
    expect(errors).toEqual([]);
  } finally {
    await browser.close();
    fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
}

test("browser file import opens a full-resolution editable image and project round-trip preserves edits", async () => {
  await runLibrary(async ({ page, id, directory, image }) => {
    await (await page.$("#open-file")).uploadFile(image);
    await page.waitForSelector("#preview img");
    const original = await page.evaluate(async () => {
      const store = await import("./store.js"), id = new URL(location.href).searchParams.get("id"), record = await store.getRecord(id);
      record.editorDocument.objects.push({ id:"redaction",type:"redact",x:40,y:40,width:100,height:80,color:"#111111",stroke:4 });
      await store.updateRecord(record);
      return { id, dimensions:record.report.dimensionsPx, state:record.state };
    });
    expect(original).toMatchObject({ state:"complete",dimensions:{width:640,height:360} });
    await page.reload(); await page.waitForSelector("#save-project");
    const session = await page.createCDPSession();
    const downloads = path.join(directory, "downloads"); fs.mkdirSync(downloads);
    await session.send("Page.setDownloadBehavior", { behavior:"allow", downloadPath:downloads });
    await page.click("#save-project");
    await expect.poll(() => fs.readdirSync(downloads).find(n => n.endsWith(".winshot.json")), { timeout:15000 }).toBeTruthy();
    const project = path.join(downloads, fs.readdirSync(downloads).find(n => n.endsWith(".winshot.json")));
    const saved = JSON.parse(fs.readFileSync(project, "utf8"));
    expect(saved.editorDocument.objects).toHaveLength(1);
    expect(saved).not.toHaveProperty("semantics");
    await page.goto(`chrome-extension://${id}/src/library.html`);
    await (await page.$("#open-file")).uploadFile(project);
    await page.waitForSelector("#preview img");
    const restored = await page.evaluate(async () => {
      const store = await import("./store.js"), id = new URL(location.href).searchParams.get("id"), record = await store.getRecord(id), tiles = await store.getTiles(id);
      const {renderEditedCapture} = await import("./image-editor.js");
      const output = await renderEditedCapture(record,tiles,record.editorDocument);
      const {exportPng} = await import("./exporters.js");
      const bitmap = await createImageBitmap(await exportPng(output.record,output.tiles));
      const canvas = new OffscreenCanvas(bitmap.width,bitmap.height),ctx=canvas.getContext("2d");ctx.drawImage(bitmap,0,0);bitmap.close();
      return {id,objects:record.editorDocument.objects.length,redacted:[...ctx.getImageData(80,80,1,1).data],source:[...ctx.getImageData(250,200,1,1).data]};
    });
    expect(restored.id).not.toBe(original.id); expect(restored.objects).toBe(1);
    expect(restored.redacted).toEqual([17,17,17,255]);expect(restored.source).toEqual([32,144,224,255]);
  });
});

test("invalid projects reject forged dimensions and missing tiles without adding History entries", async () => {
  await runLibrary(async ({ page, directory, bytes }) => {
    const input = {format:"winshot-browser-project",version:1,title:"broken",width:639,height:360,tiles:[{index:0,destXPx:0,destYPx:0,widthPx:639,heightPx:360,png:Buffer.from(bytes).toString("base64")}]};
    const filename=path.join(directory,"broken.winshot.json");fs.writeFileSync(filename,JSON.stringify(input));
    await (await page.$("#open-file")).uploadFile(filename);
    await page.waitForFunction(()=>document.querySelector("#status").classList.contains("error"));
    expect(await page.$eval("#status",n=>n.textContent)).toMatch(/dimensions do not match/);
    expect(await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length)).toBe(0);
    input.width=641;input.tiles[0].widthPx=640;fs.writeFileSync(filename,JSON.stringify(input));
    await (await page.$("#open-file")).uploadFile(filename);
    await page.waitForFunction(()=>document.querySelector("#status").textContent.includes("missing tile coverage"));
    expect(await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length)).toBe(0);
    const atomic=await page.evaluate(async()=>{
      const s=await import("./store.js"),id=crypto.randomUUID();let failed=false;
      try{await s.putImportedCapture({id},[{index:0,blob:new Blob(["a"])},{index:0,blob:new Blob(["b"])}]);}catch{failed=true;}
      return {failed,record:await s.getRecord(id),tiles:(await s.getTiles(id)).length};
    });
    expect(atomic).toEqual({failed:true,record:undefined,tiles:0});
    const syncFailure=await page.evaluate(async()=>{
      const s=await import("./store.js"),id=crypto.randomUUID(),native=IDBObjectStore.prototype.add;let failed=false;
      IDBObjectStore.prototype.add=function(...args){if(this.name==="tiles")throw new DOMException("Import quota exhausted","QuotaExceededError");return native.apply(this,args);};
      try{await s.putImportedCapture({id},[{index:0,blob:new Blob(["a"])}]);}catch{failed=true;}finally{IDBObjectStore.prototype.add=native;}
      return {failed,record:await s.getRecord(id),tiles:(await s.getTiles(id)).length};
    });
    expect(syncFailure).toEqual({failed:true,record:undefined,tiles:0});
  });
});

test("editable partial projects reopen with their original coverage and warning", async () => {
  await runLibrary(async ({page,directory,bytes}) => {
    const project=await page.evaluate(async bytes=>{
      const {saveProject}=await import("./project-file.js");
      const record={state:"partial",sourceTitle:"Stopped capture",fileName:"stopped",report:{dimensionsPx:{width:640,height:720},warnings:["Capture was stopped."]}};
      const tile={index:0,destXPx:0,destYPx:0,widthPx:640,heightPx:360,blob:new Blob([new Uint8Array(bytes)],{type:"image/png"})};
      return [...new Uint8Array(await(await saveProject(record,[tile])).arrayBuffer())];
    },bytes);
    const filename=path.join(directory,"partial.winshot.json");fs.writeFileSync(filename,Buffer.from(project));
    await(await page.$("#open-file")).uploadFile(filename);await page.waitForSelector("#preview img");
    const record=await page.evaluate(async()=>(await import("./store.js")).getRecord(new URL(location.href).searchParams.get("id")));
    expect(record.state).toBe("partial");expect(record.report.coverage).toMatchObject({complete:false,ratio:.5});
    expect(record.report.warnings.join(" ")).toContain("partial capture");
    expect(await page.$eval("#state",n=>n.textContent)).toBe("partial");
    const failedSave=await page.evaluate(async()=>{
      const s=await import("./store.js"),r=await s.getRecord(new URL(location.href).searchParams.get("id")),tiles=await s.getTiles(r.id);
      const {saveProject}=await import("./project-file.js");
      r.state="failed";r.error="The final capture readback failed.";r.report.dimensionsPx.height=360;r.editorDocument.crop.height=360;
      return JSON.parse(await(await saveProject(r,tiles)).text());
    });
    expect(failedSave.state).toBe("partial");expect(failedSave.warnings).toContain("The final capture readback failed.");
  });
});

test("oversized image headers are rejected before allocating their decoded pixels", async () => {
  await runLibrary(async ({page,directory,bytes})=>{
    await page.evaluate(()=>{window.decodeAttempts=0;const native=window.createImageBitmap;window.createImageBitmap=(...args)=>{window.decodeAttempts++;return native(...args);};});
    const huge=Buffer.from(bytes);huge.writeUInt32BE(200_000,16);huge.writeUInt32BE(200_000,20);
    const filename=path.join(directory,"oversized.png");fs.writeFileSync(filename,huge);
    await(await page.$("#open-file")).uploadFile(filename);
    await page.waitForFunction(()=>document.querySelector("#status").classList.contains("error"));
    expect(await page.$eval("#status",n=>n.textContent)).toMatch(/too large/);
    expect(await page.evaluate(()=>window.decodeAttempts)).toBe(0);
  });
});

test("JPEG WebP GIF and BMP files pass header checks and open their decoded image", async () => {
  await runLibrary(async ({page,id,directory,bytes})=>{
    const encoded=await page.evaluate(async bytes=>{
      const bitmap=await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:"image/png"})),canvas=new OffscreenCanvas(640,360);
      canvas.getContext("2d").drawImage(bitmap,0,0);bitmap.close();const out=[];
      for(const type of ["image/jpeg","image/webp"])out.push([...new Uint8Array(await(await canvas.convertToBlob({type,quality:.95})).arrayBuffer())]);
      return out;
    },bytes);
    const bmp=Buffer.alloc(70);bmp.write("BM");bmp.writeUInt32LE(70,2);bmp.writeUInt32LE(54,10);bmp.writeUInt32LE(40,14);bmp.writeInt32LE(2,18);bmp.writeInt32LE(2,22);bmp.writeUInt16LE(1,26);bmp.writeUInt16LE(24,28);bmp.writeUInt32LE(16,34);
    for(const offset of [54,57,62,65])bmp[offset+2]=255;
    const fixtures=[{name:"test.jpg",bytes:Buffer.from(encoded[0]),width:640,height:360},{name:"test.webp",bytes:Buffer.from(encoded[1]),width:640,height:360},{name:"test.gif",bytes:Buffer.from("R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7","base64"),width:1,height:1},{name:"test.bmp",bytes:bmp,width:2,height:2}];
    for(const fixture of fixtures){
      const filename=path.join(directory,fixture.name);fs.writeFileSync(filename,fixture.bytes);
      await page.goto(`chrome-extension://${id}/src/library.html`);
      await(await page.$("#open-file")).uploadFile(filename);await page.waitForSelector("#preview img");
      const result=await page.evaluate(async()=>{
        const s=await import("./store.js"),id=new URL(location.href).searchParams.get("id"),r=await s.getRecord(id),t=(await s.getTiles(id))[0],bitmap=await createImageBitmap(t.blob),c=new OffscreenCanvas(bitmap.width,bitmap.height),ctx=c.getContext("2d");ctx.drawImage(bitmap,0,0);bitmap.close();return {size:r.report.dimensionsPx,pixel:[...ctx.getImageData(0,0,1,1).data]};
      });
      expect(result.size,fixture.name).toEqual({width:fixture.width,height:fixture.height});
      if(fixture.width===640)for(let i=0;i<3;i++)expect(Math.abs(result.pixel[i]-[32,144,224][i]),fixture.name).toBeLessThanOrEqual(10);
      if(fixture.name.endsWith("bmp"))expect(result.pixel).toEqual([255,0,0,255]);
    }
  });
});

test("pasting an actual clipboard image opens its pixels without a native application", async () => {
  await runLibrary(async ({ browser, page, id, bytes }) => {
    await browser.defaultBrowserContext().overridePermissions(`chrome-extension://${id}`,["clipboard-read","clipboard-write"]);
    await page.bringToFront();
    await page.evaluate(async bytes=>navigator.clipboard.write([new ClipboardItem({"image/png":new Blob([new Uint8Array(bytes)],{type:"image/png"})})]),bytes);
    await page.click("#paste-image");await page.waitForSelector("#preview img");
    const data=await page.evaluate(async()=>{const s=await import("./store.js"),r=await s.getRecord(new URL(location.href).searchParams.get("id"));return {size:r.report.dimensionsPx,source:r.sourceTitle};});
    expect(data).toEqual({size:{width:640,height:360},source:"Clipboard image"});
  });
});
