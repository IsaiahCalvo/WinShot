import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "@playwright/test";
import { zipSync } from "../../src/vendor/fflate.js";
import { launchCaptureBrowser } from "./launch-browser.mjs";

async function nativeFixture(check) {
  const directory=fs.mkdtempSync(path.join(os.tmpdir(),"winshot-native-import-")),browser=await launchCaptureBrowser(path.join(directory,"profile"));
  try{
    const id=await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH||fileURLToPath(new URL("../../dist/winshot-capture",import.meta.url))),page=await browser.newPage(),errors=[];
    page.on("pageerror",e=>errors.push(e.message));
    const library=`chrome-extension://${id}/src/library.html`;await page.goto(library);
    const png=Buffer.from(await page.evaluate(async()=>{const c=new OffscreenCanvas(64,48),ctx=c.getContext("2d");ctx.fillStyle="#de6720";ctx.fillRect(0,0,64,48);return [...new Uint8Array(await(await c.convertToBlob({type:"image/png"})).arrayBuffer())];}));
    function archive(name,annotations,entries={}){
      const filename=path.join(directory,`${name}.winshot`);
      fs.writeFileSync(filename,zipSync({"source.png":[png,{level:0}],"annotations.json":[new TextEncoder().encode(JSON.stringify({version:1,annotations})),{level:6}],...entries}));return filename;
    }
    await check({page,library,directory,png,archive});expect(errors).toEqual([]);
  }finally{await browser.close();fs.rmSync(directory,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
}

test("native Windows project ZIP imports supported geometry and exact black redaction as editable objects",async()=>{
  await nativeFixture(async({page,library,archive,directory})=>{
    const filename=archive("native",[
      {type:"rectangle",rect:[20,5,18,20],color:"#FF2080E0",thickness:2,fill:"None",tx:2,ty:3,z:0},
      {type:"ellipse",rect:[44,4,14,14],color:"#FF00B060",thickness:2,fill:"None",z:1},
      {type:"freehand",points:[[4,38],[14,42],[24,38]],color:"#FF8040C0",thickness:2,z:2},
      {type:"arrow",points:[[36,36],[54,36]],head:"vShape",style:"Thin",color:"#FF101010",thickness:2,z:3},
      {type:"redact",rect:[1,2,10,8],style:"BlackOut",tx:3,ty:4,z:4}
    ]);
    await(await page.$("#open-file")).uploadFile(filename);await page.waitForSelector("#preview img");
    const result=await page.evaluate(async()=>{
      const s=await import("./store.js"),id=new URL(location.href).searchParams.get("id"),r=await s.getRecord(id),t=await s.getTiles(id),{renderEditedCapture}=await import("./image-editor.js"),{exportPng}=await import("./exporters.js"),out=await renderEditedCapture(r,t,r.editorDocument),b=await createImageBitmap(await exportPng(out.record,out.tiles)),c=new OffscreenCanvas(64,48),ctx=c.getContext("2d");ctx.drawImage(b,0,0);b.close();
      const redact=r.editorDocument.objects.find(o=>o.type==="redact");return {types:r.editorDocument.objects.map(o=>o.type),position:r.editorDocument.objects[0],black:[...ctx.getImageData(6,8,1,1).data],source:[...ctx.getImageData(18,20,1,1).data],fill:document.querySelector(`[data-object-id='${redact.id}'] rect`).getAttribute("fill")};
    });
    expect(result).toMatchObject({types:["rectangle","ellipse","pen","arrow","redact"],position:{x:22,y:8},black:[0,0,0,255],source:[222,103,32,255],fill:"#000000"});
    const roundtrip=await page.evaluate(async()=>{const s=await import("./store.js"),id=new URL(location.href).searchParams.get("id"),r=await s.getRecord(id),t=await s.getTiles(id),{saveProject}=await import("./project-file.js");return await(await saveProject(r,t)).text();});
    const project=path.join(directory,"roundtrip.winshot.json");fs.writeFileSync(project,roundtrip);await page.goto(library);await(await page.$("#open-file")).uploadFile(project);await page.waitForSelector("#preview img");
    expect(await page.evaluate(async()=>{const s=await import("./store.js"),r=await s.getRecord(new URL(location.href).searchParams.get("id"));return r.editorDocument.objects.find(o=>o.type==="redact").redactionColor;})).toBe("#000000");
  });
});

test("unsupported native redaction effects and styles reject the entire project without dropping private annotations",async()=>{
  await nativeFixture(async({page,archive})=>{
    const base={type:"rectangle",rect:[2,2,10,10],color:"#FF000000",thickness:2,fill:"None"};
    for(const [name,annotation]of[["blur",{type:"redact",rect:[2,2,10,10],style:"BlurSmooth"}],["filled",{...base,fill:"Solid"}],["rotated",{...base,angle:25}],["image",{type:"image",rect:[0,0,20,20],image:0}],["alpha",{...base,color:"#80000000"}]]){
      await(await page.$("#open-file")).uploadFile(archive(name,[base,annotation]));await page.waitForFunction(()=>document.querySelector("#status").classList.contains("error"));
      expect(await page.$eval("#status",n=>n.textContent)).toMatch(/cannot preserve/);
      expect(await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length)).toBe(0);
    }
  });
});

test("native ZIP checksum, path, ZIP64 and forged inflate size fail before history writes",async()=>{
  await nativeFixture(async({page,archive,directory,png})=>{
    const valid=fs.readFileSync(archive("empty",[])),checksum=Buffer.from(valid),zip64=Buffer.from(valid),bomb=Buffer.from(valid);
    let sourceCentral=-1,jsonCentral=-1;
    for(let p=0;p+46<valid.length;p++)if(valid.readUInt32LE(p)===0x02014b50){const n=valid.readUInt16LE(p+28),name=valid.toString("utf8",p+46,p+46+n);if(name==="source.png")sourceCentral=p;if(name==="annotations.json")jsonCentral=p;}
    const localSource=checksum.readUInt32LE(sourceCentral+42);checksum.writeUInt32LE(1,sourceCentral+16);checksum.writeUInt32LE(1,localSource+14);
    zip64.writeUInt32LE(0xffffffff,sourceCentral+24);
    const localJson=bomb.readUInt32LE(jsonCentral+42);bomb.writeUInt32LE(1,jsonCentral+24);bomb.writeUInt32LE(1,localJson+22);
    const pathBytes=zipSync({"../source.png":[png,{level:0}],"annotations.json":new TextEncoder().encode('{"version":1,"annotations":[]}')});
    for(const[name,bytes]of[["bad-crc",checksum],["zip64",zip64],["inflate-bounds",bomb],["path",pathBytes],["truncated",valid.subarray(0,valid.length-4)]]){
      const filename=path.join(directory,`${name}.winshot`);fs.writeFileSync(filename,bytes);await(await page.$("#open-file")).uploadFile(filename);await page.waitForFunction(()=>document.querySelector("#status").classList.contains("error"));
      expect(await page.$eval("#status",n=>n.textContent)).toMatch(/checksum|ZIP64|declared size|archive paths|complete Windows project ZIP/);
      expect(await page.evaluate(async()=>(await(await import("./store.js")).listRecords()).length)).toBe(0);
    }
  });
});
