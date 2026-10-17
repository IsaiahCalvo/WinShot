import {test} from "@playwright/test";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import assert from "node:assert/strict";
import http from "node:http";
import {fileURLToPath} from "node:url";
import {launchCaptureBrowser} from "./launch-browser.mjs";

const here=path.dirname(fileURLToPath(import.meta.url));
const extensionPath=process.env.WINSHOT_EXTENSION_PATH||path.resolve(here,"../../dist/winshot-capture");
const variants=["webgl1","webgl2","idle","translucent","rounded-filter"];
const html=`<!doctype html><style>html,body{margin:0;background:rgb(19,87,155)}body{height:4200px}canvas{position:fixed;top:0;left:0;width:1000px;height:900px;display:block}.rounded-filter{border-radius:35px;filter:brightness(.8)}</style><canvas width=1000 height=900 style="visibility:visible;opacity:1;pointer-events:auto"></canvas><script>
const variant=new URL(location.href).searchParams.get('variant'),canvas=document.querySelector('canvas');
if(variant==='translucent')canvas.style.setProperty('opacity','.5','important');
if(variant==='rounded-filter')canvas.className='rounded-filter';
const gl=canvas.getContext(variant==='webgl2'?'webgl2':'webgl',{preserveDrawingBuffer:false});window.supported=!!gl;
window.originalCanvas=canvas;window.originalContext=gl;window.originalStyle=canvas.style.cssText;window.getContextNative=HTMLCanvasElement.prototype.getContext;window.originalDraw=gl?.clear;
window.paintCount=0;window.contextLost=0;canvas.addEventListener('webglcontextlost',()=>window.contextLost++);
window.color=[.86,.04,.12,1];if(gl){function paint(){window.paintCount++;gl.clearColor(...window.color);gl.clear(gl.COLOR_BUFFER_BIT);if(variant!=='idle')requestAnimationFrame(paint)}window.repaint=()=>requestAnimationFrame(paint);requestAnimationFrame(paint)}
</script>`;
const server=http.createServer((request,response)=>{response.writeHead(200,{"Content-Type":"text/html","Cache-Control":"no-store"});response.end(html);});
test.beforeAll(async()=>new Promise(resolve=>server.listen(0,"127.0.0.1",resolve)));
test.afterAll(async()=>{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));});
async function pixels(worker,dataUrl){return worker.evaluate(async url=>{const bitmap=await createImageBitmap(await(await fetch(url)).blob()),canvas=new OffscreenCanvas(bitmap.width,bitmap.height),context=canvas.getContext("2d");context.drawImage(bitmap,0,0);bitmap.close();return {x200:[...context.getImageData(200,200,1,1).data],width:canvas.width,height:canvas.height};},dataUrl);}

for(const variant of variants)test(`native ${variant} surface preserves middle-tile pixels and foreground playback`,async()=>{
 const profile=fs.mkdtempSync(path.join(os.tmpdir(),"winshot-canvas-restoration-"));let browser;
 try{
  browser=await launchCaptureBrowser(profile,{width:1200,height:1000});
  const extensionId=await browser.installExtension(extensionPath),extension=(await browser.extensions()).get(extensionId);
  const worker=await(await browser.waitForTarget(target=>target.type()==='service_worker'&&target.url().endsWith('/src/background.js'))).worker();
  const result={variant};const page=await browser.newPage();
   await page.goto(`http://127.0.0.1:${server.address().port}/?variant=${variant}`);
   assert.equal(await page.evaluate(()=>supported),true,'real WebGL required');await page.waitForFunction(()=>window.paintCount>0);
   const [tab]=await worker.evaluate(url=>chrome.tabs.query({url}),page.url());
   await worker.evaluate(async({id,windowId})=>{await chrome.tabs.update(id,{active:true});await chrome.windows.update(windowId,{focused:true})},tab);
   const beforeData=await worker.evaluate(id=>chrome.tabs.captureVisibleTab(id,{format:'png'}),tab.windowId);result.before=await pixels(worker,beforeData);
   assert.ok(result.before.x200[0]>70,'fixture source WebGL must paint a red surface');
   await new Promise(resolve=>setTimeout(resolve,550));
   const known=new Set(browser.targets()),popupWait=browser.waitForTarget(t=>!known.has(t)&&t.url().endsWith('/src/popup.html'));
   await extension.triggerAction(page);const popup=await(await popupWait).asPage();await popup.waitForSelector('body[data-ready="true"]');
   const editorWait=browser.waitForTarget(t=>!known.has(t)&&t.url().includes('/src/editor.html?id='),{timeout:60000});const start=performance.now();await popup.click('[data-mode="full-page"]');const editor=await(await editorWait).asPage();
   await editor.waitForFunction(()=>[...document.querySelectorAll('#preview img')].length>0&&[...document.querySelectorAll('#preview img')].every(n=>n.complete&&n.naturalWidth>0),{timeout:30000});
   result.capture=await editor.evaluate(async()=>{const s=await import(chrome.runtime.getURL('src/store.js')),id=new URL(location.href).searchParams.get('id'),r=await s.getRecord(id),tiles=await s.getTiles(id),out=[];for(const index of [0,1]){const t=tiles.find(t=>t.index===index),b=await createImageBitmap(t.blob),c=new OffscreenCanvas(b.width,b.height),x=c.getContext('2d');x.drawImage(b,0,0);b.close();out.push({index,pixel:[...x.getImageData(200,200,1,1).data]})}return {state:r.state,report:r.report,pixels:out}});
   assert.equal(result.capture.state,'complete');assert.equal(result.capture.report.coverage.complete,true);assert.equal(result.capture.report.restoration,'verified by read-back');
   const tolerance=variant==='translucent'?1:0;
   assert.ok(result.capture.pixels[0].pixel.every((v,i)=>Math.abs(v-result.before.x200[i])<=tolerance),'first tile matches native pixels; translucent blending permits one rounding level');
   assert.deepEqual(result.capture.pixels[1].pixel,[19,87,155,255],'middle tile hides fixed surface: zero live/frozen canvas leakage');
   await worker.evaluate(async({id,windowId})=>{await chrome.tabs.update(id,{active:true});await chrome.windows.update(windowId,{focused:true})},tab);
   result.visible=await page.evaluate(()=>({visibility:document.visibilityState,focus:document.hasFocus(),count:paintCount}));assert.equal(result.visible.visibility,'visible');assert.equal(result.visible.focus,true);
   await page.evaluate(()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r))));
   const foreground=await worker.evaluate(async windowId=>{await new Promise(r=>setTimeout(r,550));return chrome.tabs.captureVisibleTab(windowId,{format:'png'})},tab.windowId);
   await test.info().attach('native-foreground',{body:Buffer.from(foreground.split(',')[1],'base64'),contentType:'image/png'});result.foreground=await pixels(worker,foreground);
   assert.deepEqual(result.foreground.x200,result.before.x200,'native foreground preserves original pixels, including non-preserved idle buffer');
   result.restored=await page.evaluate(()=>({same:originalCanvas===document.querySelector('canvas'),contextSame:originalCanvas.getContext(originalContext instanceof WebGL2RenderingContext?'webgl2':'webgl')===originalContext,lost:originalContext.isContextLost(),contextLost,styleExact:originalCanvas.style.cssText===originalStyle,nativeGetContext:HTMLCanvasElement.prototype.getContext===getContextNative,nativeClear:originalContext.clear===originalDraw,snapshots:document.querySelectorAll('[data-winshot-surface]').length,styles:document.querySelectorAll('style[data-winshot-capture]').length,x:scrollX,y:scrollY,count:paintCount}));
   assert.deepEqual({...result.restored,count:0},{same:true,contextSame:true,lost:false,contextLost:0,styleExact:true,nativeGetContext:true,nativeClear:true,snapshots:0,styles:0,x:0,y:0,count:0});
   if(variant!=='idle')assert.ok(result.restored.count>result.visible.count,'real source GPU animation resumes');
   await page.evaluate(idle=>{window.color=[.04,.86,.12,1];if(idle)window.repaint();},variant==='idle');
   await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
   await new Promise(resolve=>setTimeout(resolve,550));
   const changed=await worker.evaluate(windowId=>chrome.tabs.captureVisibleTab(windowId,{format:'png'}),tab.windowId);
   result.newApplicationPaint=await pixels(worker,changed);
   const expected=variant==='translucent'?[15,153,93,255]:variant==='rounded-filter'?[8,175,25,255]:[10,219,31,255];
   assert.ok(result.newApplicationPaint.x200.every((value,index)=>Math.abs(value-expected[index])<=2),'the same original context must paint a new green frame after capture');
   await test.info().attach('pixel-and-restoration-proof',{body:JSON.stringify(result,null,2),contentType:'application/json'});
 }finally{await browser?.close();fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
});
