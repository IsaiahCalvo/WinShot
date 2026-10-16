// Repeated real MV3 captures in a disposable profile. Never uses a personal browser.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import http from "node:http";
import crypto from "node:crypto";
import { parseArgs } from "node:util";
import { fileURLToPath } from "node:url";
import { launchCaptureBrowser } from "../tests/e2e/launch-browser.mjs";

const { values } = parseArgs({ options: {
  extension: { type: "string", default: fileURLToPath(new URL("../dist/winshot-capture", import.meta.url)) },
  label: { type: "string", default: "candidate" }, repeats: { type: "string", default: "10" },
  scenario: { type: "string", default: "complex" }, output: { type: "string", default: "output/capture-benchmark" },
  url: { type: "string" }, profile: { type: "boolean", default: false }
} });
const repeats = Number(values.repeats);
if (!Number.isInteger(repeats) || repeats < 1 || repeats > 100) throw new Error("repeats must be 1–100");
if (!/^[a-z0-9_.-]+$/i.test(values.label)) throw new Error("label must contain only letters, numbers, dots, underscores and hyphens");
if (!["static", "complex", "continuous"].includes(values.scenario)) throw new Error("scenario must be static, complex or continuous");
if (values.url && !/^https?:\/\//i.test(values.url)) throw new Error("url must be HTTP(S)");
const output = path.resolve(values.output);
fs.mkdirSync(output, { recursive: true });
const extensionRoot = path.resolve(values.extension);
function bundleHash(directory) {
  const hash=crypto.createHash('sha256');
  function visit(folder){for(const entry of fs.readdirSync(folder,{withFileTypes:true}).sort((a,b)=>a.name.localeCompare(b.name))){const filename=path.join(folder,entry.name);if(entry.isDirectory())visit(filename);else if(entry.isFile()){hash.update(path.relative(directory,filename).split(path.sep).join('/'));hash.update('\0');hash.update(fs.readFileSync(filename));hash.update('\0');}}}
  visit(directory);return hash.digest('hex');
}
const extensionSha256 = bundleHash(extensionRoot);
const profileRoot = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-capture-benchmark-"));
const colors = [[220,30,80],[30,170,90],[25,110,220],[165,50,210],[220,105,20],[10,170,170],[130,110,35]];
const html = `<!doctype html><html><head><meta charset="utf-8"><title>WinShot repeatable scrolling benchmark</title><style>
*{box-sizing:border-box}html,body{margin:0;background:white;font:16px Arial;color:#172033}
header{position:fixed;top:0;left:0;width:100%;height:54px;background:#ffcf33;z-index:10;padding:15px}
footer{position:fixed;bottom:0;left:0;width:100%;height:46px;background:#33c89b;z-index:10;padding:12px}
section{height:800px;position:relative;overflow:hidden;border-bottom:1px solid #556}
.marker{position:absolute;left:100px;top:150px;width:80px;height:48px;opacity:0}
.grid{position:absolute;top:250px;left:100px;width:900px;display:grid;grid-template-columns:repeat(20,1fr);gap:2px}
.cell{height:15px;background:#e9eef4;border-bottom:1px solid #bbc9da}.cell span{display:block}
</style></head><body><header>FIXED HEADER — ONCE</header><main id="motion-root">${colors.map((color,i)=>`<section><h2>SECTION ${i+1}</h2><div class="marker" style="background:rgb(${color})"></div><div class="grid">${Array.from({length:400},()=>'<div class="cell"><span><i></i></span></div>').join("")}</div></section>`).join("")}</main><footer>FIXED FOOTER — ONCE</footer><script>
const observer=new IntersectionObserver(entries=>{for(const entry of entries)if(entry.isIntersecting)entry.target.style.opacity='1'});
document.querySelectorAll('.marker').forEach(node=>observer.observe(node));
window.benchmarkTicks=0;
${values.scenario !== "static" ? `const root=document.querySelector('#motion-root');function tick(){window.benchmarkTicks++;for(let i=0;i<${values.scenario === "continuous" ? 6 : 60};i++)root.style.setProperty('--motion-'+(i%6),String(window.benchmarkTicks+i));${values.scenario === "continuous" ? "requestAnimationFrame(tick);" : ""}}${values.scenario === "continuous" ? "requestAnimationFrame(tick);" : "addEventListener('scroll',tick);"}` : ""}
</script></body></html>`;
const server = http.createServer((request,response) => { response.writeHead(200,{"content-type":"text/html; charset=utf-8","cache-control":"no-store"});response.end(html); });
await new Promise(resolve=>server.listen(0,"127.0.0.1",resolve));
const url = values.url || `http://127.0.0.1:${server.address().port}/`;
const proxy = values.url && (process.env.HTTPS_PROXY || process.env.HTTP_PROXY);
const rows = [];
let browser;
const percentile = (numbers,p) => {const sorted=[...numbers].sort((a,b)=>a-b);return sorted[Math.max(0,Math.ceil(sorted.length*p)-1)] ?? null;};
try {
  browser = await launchCaptureBrowser(profileRoot,{width:1200,height:1000},{args:proxy ? [`--proxy-server=${proxy}`] : []});
  const id=await browser.installExtension(extensionRoot);
  const extension=(await browser.extensions()).get(id);
  const worker=await (await browser.waitForTarget(target=>target.type()==="service_worker"&&target.url().endsWith("/src/background.js"))).worker();
  const version=await worker.evaluate(()=>chrome.runtime.getManifest().version);
  // Wrap native APIs only to time the unmodified native call. No substituted pixels,
  // clocks, messages, delays or application results. Trace overhead is shared by A/B.
  await worker.evaluate(()=>{
    globalThis.benchmarkTrace=[];
    for(const [object,key] of [[chrome.tabs,"captureVisibleTab"],[chrome.tabs,"sendMessage"],[chrome.scripting,"executeScript"]]){
      const original=object[key];
      object[key]=async function(...args){
        const entry={api:key,type:key==='sendMessage'?args[1]?.type:null,x:args[1]?.x,y:args[1]?.y,start:performance.now()};
        globalThis.benchmarkTrace.push(entry);
        try{return await original.apply(this,args);}catch(error){entry.error=String(error);throw error;}finally{entry.end=performance.now();entry.durationMs=entry.end-entry.start;}
      };
    }
  });
  for(let index=0;index<repeats;index++){
    const page=await browser.newPage();let editor,popup,client;
    const row={label:values.label,scenario:values.url?'public':values.scenario,url,attempt:index+1,version};
    const errors=[];page.on('pageerror',error=>errors.push(error.message));
    try{
      if(values.url)await page.setCacheEnabled(false);
      const response=await page.goto(url,{waitUntil:'domcontentloaded',timeout:45000});
      if(!response?.ok()&&response?.status()!==304)throw new Error(`Source HTTP ${response?.status()}`);
      if(/^(just a moment|access denied|attention required|verify you are human)/i.test(await page.title()))throw new Error('Source access challenge; benchmark did not proceed');
      await page.evaluate(()=>Promise.race([document.fonts.ready,new Promise(resolve=>setTimeout(resolve,3000))]));
      await new Promise(resolve=>setTimeout(resolve,values.url?1200:400));
      await page.evaluate(()=>scrollTo(0,230));
      row.source=await page.evaluate(()=>({title:document.title,url:location.href,x:scrollX,y:scrollY,width:document.scrollingElement.scrollWidth,height:document.scrollingElement.scrollHeight,nodes:document.querySelectorAll('*').length,ticks:window.benchmarkTicks}));
      await page.bringToFront();
      const prior=new Set(browser.targets());
      const popupWait=browser.waitForTarget(target=>!prior.has(target)&&target.type()==='page'&&target.url().endsWith('/src/popup.html'));
      await extension.triggerAction(page);popup=await (await popupWait).asPage();
      await popup.waitForSelector("body[data-ready='true']");
      await popup.evaluate(()=>{document.querySelector('#capture-height').value='80000';document.querySelector('#capture-timeout').value='2';document.querySelector('#capture-tiles').value='120';});
      await worker.evaluate(()=>{globalThis.benchmarkTrace=[];});
      if(values.profile&&index===0){client=await page.createCDPSession();await client.send('Profiler.enable');await client.send('Profiler.start');}
      const controller=new AbortController();
      const editorWait=browser.waitForTarget(target=>!prior.has(target)&&target.type()==='page'&&target.url().includes('/src/editor.html?id='),{timeout:180000,signal:controller.signal});
      editorWait.catch(()=>{});
      const started=performance.now();
      try{
        await popup.click("[data-mode='full-page']");editor=await (await editorWait).asPage();
        row.editorOpenedMs=performance.now()-started;
        await editor.waitForFunction(()=>{
          const state=document.querySelector('#state')?.textContent.trim()||'';
          if(state.toLowerCase()==='failed')return true;
          const images=[...document.querySelectorAll('#preview img')];
          const overlay=document.querySelector('#preview .annotation-overlay');
          const box=overlay?.getBoundingClientRect();
          const editorLayoutReady=!document.querySelector('[data-tool]')||Boolean(box?.width>0&&box?.height>0);
          return state&&!/^loading\b/i.test(state)&&images.length>0&&images.every(image=>image.complete&&image.naturalWidth>0)&&editorLayoutReady;
        },{timeout:Math.max(1,Math.min(60000,180000-(performance.now()-started)))});
        if((await editor.$eval('#state',node=>node.textContent.trim())).toLowerCase()==='failed')throw new Error('Editor failed to load the capture');
        row.previewLoadedMs=performance.now()-started;
        let decodeTimer;
        try{
          await Promise.race([
            editor.evaluate(()=>Promise.all([...document.querySelectorAll('#preview img')].map(image=>image.decode()))),
            new Promise((_,reject)=>{decodeTimer=setTimeout(()=>reject(new Error('Editor image decode deadline exceeded')),Math.max(1,Math.min(60000,180000-(performance.now()-started))));})
          ]);
        }finally{clearTimeout(decodeTimer);}
        row.elapsedMs=performance.now()-started;
      }
      finally{controller.abort();}
      if(client){const {profile}=await client.send('Profiler.stop');fs.writeFileSync(path.join(output,`${values.label}-${index+1}.cpuprofile`),JSON.stringify(profile));await client.detach();client=null;}
      row.trace=await worker.evaluate(()=>globalThis.benchmarkTrace);
      const audit=await editor.evaluate(async(expected)=>{
        const store=await import(chrome.runtime.getURL('src/store.js'));
        const id=new URL(location.href).searchParams.get('id');const record=await store.getRecord(id),tiles=await store.getTiles(id);
        const size=record.report.dimensionsPx;if(!size)throw new Error('No captured dimensions');
        if(!expected.length){
          const tileAudit=[];
          for(const tile of tiles){
            const image=await createImageBitmap(tile.blob),canvas=new OffscreenCanvas(image.width,image.height),ctx=canvas.getContext('2d',{willReadFrequently:true});ctx.drawImage(image,0,0);image.close();
            const samples=new Set();for(let y=0;y<canvas.height;y+=Math.max(1,Math.floor(canvas.height/16)))for(let x=0;x<canvas.width;x+=Math.max(1,Math.floor(canvas.width/16)))samples.add([...ctx.getImageData(x,y,1,1).data].join(','));
            const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',await tile.blob.arrayBuffer()))].map(value=>value.toString(16).padStart(2,'0')).join('');
            tileAudit.push({index:tile.index,x:tile.destXPx,y:tile.destYPx,width:tile.widthPx,height:tile.heightPx,sha256:hash,distinctSampledColors:samples.size});
          }
          return{record,audit:{dimensions:size,tileCount:tiles.length,tiles:tileAudit,scope:'Rendered-pixel sanity and complete coverage, not a known-content pixel oracle'}};
        }
        // Bounding composite to the documented exporter limit keeps this probe safe.
        if(size.width*size.height>32_000_000)throw new Error('Benchmark composite exceeds 32 MP; choose a smaller viewport/page');
        const canvas=new OffscreenCanvas(size.width,size.height),ctx=canvas.getContext('2d',{willReadFrequently:true});
        for(const tile of tiles.sort((a,b)=>a.index-b.index)){const image=await createImageBitmap(tile.blob);ctx.drawImage(image,tile.destXPx,tile.destYPx,tile.widthPx,tile.heightPx);image.close();}
        const colorRows=expected.map(()=>[]),headerRows=[],footerRows=[];
        for(let y=0;y<size.height;y++){
          const pixel=[...ctx.getImageData(110,y,1,1).data];
          expected.forEach((color,index)=>{if(color.every((value,i)=>pixel[i]===value))colorRows[index].push(y);});
          const edge=[...ctx.getImageData(10,y,1,1).data];
          if(edge[0]===255&&edge[1]===207&&edge[2]===51)headerRows.push(y);
          if(edge[0]===51&&edge[1]===200&&edge[2]===155)footerRows.push(y);
        }
        const bytes=new Uint8Array(await (await canvas.convertToBlob({type:'image/png'})).arrayBuffer());
        const hash=[...new Uint8Array(await crypto.subtle.digest('SHA-256',bytes))].map(value=>value.toString(16).padStart(2,'0')).join('');
        return {record,audit:{dimensions:size,tileCount:tiles.length,markerRows:colorRows,headerRows,footerRows,pngSha256:hash}};
      },values.url?[]:colors);
      row.state=audit.record.state;row.report=audit.record.report;row.audit=audit.audit;
      row.restored=await page.evaluate(()=>({x:scrollX,y:scrollY,captureStyles:document.querySelectorAll('style[data-winshot-capture]').length,pickers:document.querySelectorAll('[data-winshot-picker]').length,ticks:window.benchmarkTicks}));
      row.captureComplete=row.state==='complete'&&row.report.coverage?.complete===true;
      row.fullRestorationVerified=row.report.restoration==='verified by read-back';
      row.correct=row.captureComplete&&row.fullRestorationVerified&&row.restored.x===row.source.x&&row.restored.y===row.source.y&&row.restored.captureStyles===0&&row.restored.pickers===0;
      if(!values.url)row.correct&&=row.audit.markerRows.every(rows=>rows.length===48)&&row.audit.headerRows.length===54&&row.audit.footerRows.length===46&&row.audit.dimensions.width===1200&&row.audit.dimensions.height===5600;
      else row.correct&&=row.audit.tiles.some(tile=>tile.distinctSampledColors>3)&&row.audit.tileCount===row.report.tileCount;
      if(index===0)await editor.screenshot({path:path.join(output,`${values.label}-editor.png`)});
      const screenshotCalls=row.trace.filter(entry=>entry.api==='captureVisibleTab');
      const scrollCalls=row.trace.filter(entry=>entry.type==='WINSHOT_SCROLL');
      const gaps=screenshotCalls.slice(1).map((entry,index)=>entry.start-screenshotCalls[index].start);
      row.stages={readbacks:screenshotCalls.length,readbackMs:screenshotCalls.reduce((sum,entry)=>sum+entry.durationMs,0),minReadbackCallGapMs:gaps.length?Math.min(...gaps):null,quotaErrors:screenshotCalls.filter(entry=>/MAX_CAPTURE_VISIBLE_TAB_CALLS_PER_SECOND|quota/i.test(entry.error||'')).length,scrollCalls:scrollCalls.length,scrollMs:scrollCalls.reduce((sum,entry)=>sum+entry.durationMs,0),scrollP95Ms:percentile(scrollCalls.map(entry=>entry.durationMs),.95),restoreMs:row.trace.filter(entry=>entry.type==='WINSHOT_RESTORE').reduce((sum,entry)=>sum+entry.durationMs,0)};
      if(row.stages.quotaErrors)row.correct=false;
    }catch(error){row.error=String(error);row.correct=false;}
    finally{if(client)await client.detach().catch(()=>{});row.pageErrors=errors;rows.push(row);fs.writeFileSync(path.join(output,`${values.label}-runs.json`),JSON.stringify(rows,null,2));console.log(JSON.stringify({attempt:row.attempt,elapsedMs:row.elapsedMs,correct:row.correct,captureComplete:row.captureComplete,fullRestorationVerified:row.fullRestorationVerified,restoration:row.report?.restoration,state:row.state,stages:row.stages,error:row.error}));await popup?.close().catch(()=>{});await editor?.close().catch(()=>{});await page.close().catch(()=>{});}
  }
  const times=rows.filter(row=>row.correct).map(row=>row.elapsedMs);
  const summary={label:values.label,scenario:values.url?'public':values.scenario,url,version,extensionPath:extensionRoot,extensionSha256,measurement:'Popup click to non-Loading editor state, positive-sized annotation overlay when present, and all preview img.decode() promises resolved; editorOpenedMs and previewLoadedMs separately record earlier stages',networkCachePolicy:values.url?'Browser HTTP cache disabled before each navigation':'Fixture served with Cache-Control:no-store',profileFirstAttempt:values.profile,browser:await browser.version(),platform:process.platform,viewport:{width:1200,height:1000},samples:rows.length,passed:times.length,failed:rows.length-times.length,p50Ms:percentile(times,.5),p95Ms:percentile(times,.95),minMs:times.length?Math.min(...times):null,maxMs:times.length?Math.max(...times):null,percentileMethod:'nearest rank, passing samples only; failures are separately counted; small-sample p95 is descriptive, not a population guarantee',fixtureSha256:crypto.createHash('sha256').update(html).digest('hex'),capturedAt:new Date().toISOString(),graphics:{softwareGL:process.env.WINSHOT_TEST_SOFTWARE_GL||'0',softwareCompositing:process.env.WINSHOT_TEST_SOFTWARE_COMPOSITING||'default'}};
  summary.captureCompleteCount=rows.filter(row=>row.captureComplete).length;
  summary.fullyRestoredCount=rows.filter(row=>row.fullRestorationVerified&&row.restored?.x===row.source?.x&&row.restored?.y===row.source?.y&&!row.restored?.captureStyles&&!row.restored?.pickers).length;
  summary.unverifiedRestorationCount=rows.filter(row=>row.report&&row.report.restoration!=='verified by read-back').length;
  fs.writeFileSync(path.join(output,`${values.label}-summary.json`),JSON.stringify(summary,null,2));console.log(JSON.stringify(summary));
  if(summary.failed)process.exitCode=1;
}finally{await browser?.close();server.close();fs.rmSync(profileRoot,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
