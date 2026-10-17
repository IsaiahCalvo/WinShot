import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import PostalMime from "postal-mime";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function withImage(verify) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-sharing-"));
  const browser = await launchCaptureBrowser(path.join(directory, "profile"));
  try {
    const id = await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL("../../dist/winshot-capture", import.meta.url)));
    const page = await browser.newPage(), errors = [];
    page.on("pageerror", e => errors.push(e.message));
    page.on("console", m => { if (m.type() === "error") errors.push(m.text()); });
    await page.goto(`chrome-extension://${id}/src/library.html`);
    const recordId = await page.evaluate(async () => {
      const canvas = new OffscreenCanvas(300, 180), ctx = canvas.getContext("2d");
      ctx.fillStyle = "#23a065"; ctx.fillRect(0, 0, 300, 180);
      const {importImage} = await import("./project-file.js"), {updateRecord} = await import("./store.js");
      const record = await importImage(new File([await canvas.convertToBlob({type:"image/png"})], "sharing.png", {type:"image/png"}));
      record.sourceTitle = 'Résumé <script>alert("x")</script>'; record.sourceUrl = "https://example.com/source";
      record.editorDocument.objects.push({id:"private",type:"redact",x:20,y:20,width:60,height:60,color:"#111111",stroke:4});
      await updateRecord(record); return record.id;
    });
    await page.goto(`chrome-extension://${id}/src/editor.html?id=${recordId}`);
    await page.waitForSelector(".annotation-overlay"); await page.bringToFront();
    expect(await page.title()).toBe("WinShot Capture");
    await page.click("#share-capture"); await page.waitForSelector("#share-dialog[open]");
    await verify({browser,page,id,directory}); expect(errors).toEqual([]);
  } finally { await browser.close(); fs.rmSync(directory,{recursive:true,force:true,maxRetries:5,retryDelay:100}); }
}
async function pixel(page, bytes) {
  return page.evaluate(async bytes => {
    const b = await createImageBitmap(new Blob([new Uint8Array(bytes)],{type:"image/png"})), c = new OffscreenCanvas(b.width,b.height),x=c.getContext("2d");
    x.drawImage(b,0,0); b.close(); return [...x.getImageData(30,30,1,1).data];
  },bytes);
}

test("sharing email and offline note exports contain edited pixels and no project originals", async () => {
  await withImage(async({page,directory}) => {
    const downloads=path.join(directory,"downloads"); fs.mkdirSync(downloads);
    const session=await page.createCDPSession(); await session.send("Page.setDownloadBehavior",{behavior:"allow",downloadPath:downloads});
    await page.click("#share-email");
    await expect.poll(()=>fs.readdirSync(downloads).find(n=>n.endsWith(".eml"))).toBeTruthy();
    const eml=fs.readFileSync(path.join(downloads,fs.readdirSync(downloads).find(n=>n.endsWith(".eml"))));
    const parsed=await PostalMime.parse(eml); expect(parsed.subject).toContain("Résumé"); expect(parsed.attachments).toHaveLength(1);
    expect(parsed.headers.find(h=>h.key==="x-unsent")?.value).toBe("1"); expect(parsed.from).toBeUndefined();
    const attachment=[...new Uint8Array(parsed.attachments[0].content)]; expect(await pixel(page,attachment)).toEqual([17,17,17,255]);
    expect(eml.toString()).not.toContain("wsPJ");
    await page.click("#share-note"); await expect.poll(()=>fs.readdirSync(downloads).find(n=>n.endsWith(".html"))).toBeTruthy();
    const html=fs.readFileSync(path.join(downloads,fs.readdirSync(downloads).find(n=>n.endsWith(".html"))),"utf8");
    expect(html).toContain("&lt;script&gt;"); expect(html).not.toContain('<script>alert'); expect(html).toContain("https://example.com/source");
    const image=html.match(/src="data:image\/png;base64,([^"]+)"/)[1];
    expect(await pixel(page,[...Buffer.from(image,"base64")])).toEqual([17,17,17,255]);
  });
});

test("OneNote copy writes real PNG HTML and text clipboard formats with flattened edits", async () => {
  await withImage(async({browser,page,id}) => {
    await page.click("#share-onenote");
    await page.waitForFunction(()=>document.querySelector("#share-status").textContent.startsWith("Copied."));
    await browser.defaultBrowserContext().overridePermissions(`chrome-extension://${id}`,["clipboard-read","clipboard-write"]);
    const result=await page.evaluate(async()=>{
      const item=(await navigator.clipboard.read())[0],png=await item.getType("image/png");
      return {types:item.types,png:[...new Uint8Array(await png.arrayBuffer())],html:await(await item.getType("text/html")).text(),text:await(await item.getType("text/plain")).text()};
    });
    expect(result.types).toEqual(expect.arrayContaining(["image/png","text/html","text/plain"]));
    expect(await pixel(page,result.png)).toEqual([17,17,17,255]); expect(result.text).toContain("https://example.com/source");
    expect(result.html).not.toContain('<script>alert');
  });
});

test("share preparation supports drag download, handles share cancellation and fits mobile", async () => {
  await withImage(async({page}) => {
    // OS share sheets are outside browser automation. Test the browser handoff
    // contract and cancellation separately from the real export/clipboard tests.
    await page.evaluate(()=>{Object.defineProperty(navigator,"canShare",{configurable:true,value:()=>true}); Object.defineProperty(navigator,"share",{configurable:true,value:async options=>{window.shareFiles=options.files;throw new DOMException("Canceled","AbortError");}});});
    await page.click("#share-prepare"); await page.waitForSelector("#share-file-ready:not([hidden])");
    const drag=await page.$eval("#share-drag",n=>{const dt=new DataTransfer();n.dispatchEvent(new DragEvent("dragstart",{bubbles:true,dataTransfer:dt}));return {data:dt.getData("DownloadURL"),name:n.download};});
    expect(drag.data).toMatch(/^image\/png:sharing\.png:blob:chrome-extension:\/\//); expect(drag.name).toBe("sharing.png");
    await page.click("#share-system"); await page.waitForFunction(()=>document.querySelector("#share-status").textContent.startsWith("Sharing canceled"));
    expect(await page.evaluate(()=>window.shareFiles[0].type)).toBe("image/png");
    const evidence=process.env.WINSHOT_QA_DIR;
    if(evidence){fs.mkdirSync(evidence,{recursive:true});await page.screenshot({path:path.join(evidence,"sharing-desktop.png")});}
    await page.setViewport({width:390,height:844});
    const bounds=await page.$eval("#share-dialog",n=>{const b=n.getBoundingClientRect();return {left:b.left,right:b.right,scroll:n.scrollWidth,client:n.clientWidth};});
    expect(bounds.left).toBeGreaterThanOrEqual(0);expect(bounds.right).toBeLessThanOrEqual(390);expect(bounds.scroll).toBeLessThanOrEqual(bounds.client+1);
    if(evidence){await page.screenshot({path:path.join(evidence,"sharing-mobile.png")});}
    await page.keyboard.press("Escape");expect(await page.$eval("#share-dialog",n=>n.open)).toBe(false);
  });
});

test("closed sharing sessions cannot publish stale files or start late downloads after reopening", async () => {
  await withImage(async({page,directory}) => {
    const downloads=path.join(directory,"downloads");fs.mkdirSync(downloads);
    await(await page.createCDPSession()).send("Page.setDownloadBehavior",{behavior:"allow",downloadPath:downloads});
    await page.evaluate(async()=>{
      const {ImageEditor}=await import("./image-editor.js"),original=ImageEditor.prototype.exportInput;
      ImageEditor.prototype.exportInput=async function(...args){const result=await original.apply(this,args);window.shareGateStarted=true;await window.shareGate;window.shareGateFinished=true;return result;};
    });
    for(const action of ["#share-prepare","#share-email","#share-note"]){
      await page.evaluate(()=>{window.shareGateStarted=false;window.shareGateFinished=false;window.shareGate=new Promise(resolve=>window.finishShareGate=resolve);});
      await page.click(action);await page.waitForFunction(()=>window.shareGateStarted);
      await page.keyboard.press("Escape");await page.waitForFunction(()=>!document.querySelector("#share-dialog").open);
      await page.click("#share-capture");await page.evaluate(()=>window.finishShareGate());await page.waitForFunction(()=>window.shareGateFinished);
      expect(await page.$eval("#share-file-ready",n=>n.hidden)).toBe(true);
      expect(await page.$eval("#share-status",n=>n.textContent)).toBe("");
      expect(await page.$eval("#share-prepare",n=>n.disabled)).toBe(false);expect(fs.readdirSync(downloads)).toEqual([]);
    }
  });
});

test("sharing dialog owns keyboard shortcuts and does not undo background image edits", async () => {
  await withImage(async({page}) => {
    await page.keyboard.press("Escape");await page.click("[data-tool=redact]");
    const b=await page.$eval(".annotation-overlay",n=>{const b=n.getBoundingClientRect();return {x:b.x,y:b.y,k:b.width/Number(n.getAttribute("width"))};});
    await page.mouse.move(b.x+140*b.k,b.y+50*b.k);await page.mouse.down();await page.mouse.move(b.x+210*b.k,b.y+100*b.k,{steps:5});await page.mouse.up();
    await page.waitForFunction(()=>document.querySelector("#edit-save-state").textContent==="Saved locally");
    const documentState=()=>page.evaluate(async()=>JSON.stringify((await(await import("./store.js")).getRecord(new URL(location.href).searchParams.get("id"))).editorDocument));
    const before=await documentState();
    await page.click("#share-capture");await page.click("#share-prepare");await page.waitForSelector("#share-file-ready:not([hidden])");
    await page.focus("#share-prepare");await page.keyboard.down("Control");await page.keyboard.press("z");await page.keyboard.press("y");await page.keyboard.up("Control");await page.keyboard.press("Delete");
    expect(await documentState()).toBe(before);
    expect(await page.$eval("#share-dialog",n=>n.open)).toBe(true);
  });
});
