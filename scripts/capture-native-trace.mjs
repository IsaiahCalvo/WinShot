import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import crypto from "node:crypto";
import zlib from "node:zlib";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { launchCaptureBrowser } from "../browser-extension/tests/e2e/launch-browser.mjs";

const TRACE_CATEGORIES = Object.freeze(["toplevel", "blink.console", "devtools.timeline", "renderer.scheduler", "cc", "viz", "gpu", "media"]);
const TRACE_BUFFER_KIB = 32768;
const TRACE_FLUSH_MS = 40000;
const MAX_TRACE_BYTES = 64 * 1024 * 1024;
const OVERALL_MS = 180000;
const RESERVE_MS = 50000;

function packagedFiles(folder) {
  const files=[];
  const visit=(directory)=>{
    for(const item of fs.readdirSync(directory,{withFileTypes:true})) {
      const absolute=path.join(directory,item.name);
      if(item.isDirectory()) visit(absolute);
      else if(item.isFile()) files.push({name:path.relative(folder,absolute).split(path.sep).join("/"),bytes:fs.readFileSync(absolute)});
      else throw new Error("Package must contain regular files/directories: "+absolute);
    }
  };
  visit(folder);files.sort((left,right)=>left.name<right.name?-1:left.name>right.name?1:0);return files;
}
function sha256(bytes) { return crypto.createHash("sha256").update(bytes).digest("hex"); }
function canonicalBundleHash(files) {
  const hash=crypto.createHash("sha256");
  for(const file of files){hash.update(file.name);hash.update("\0");hash.update(file.bytes);hash.update("\0");}
  return hash.digest("hex");
}
function crc32(bytes) {
  let value=0xffffffff;
  for(const byte of bytes){value^=byte;for(let bit=0;bit<8;bit++)value=(value>>>1)^((value&1)?0xedb88320:0);}
  return (value^0xffffffff)>>>0;
}
// A deterministic, uncompressed ZIP of the exact loaded package. No extra
// dependency, platform archiver, source transformation, or generated code bytes.
function packageZip(files) {
  if(files.length>65535)throw new Error("ZIP32 file count limit exceeded");
  const local=[],central=[];let offset=0;
  for(const file of files){
    const name=Buffer.from(file.name,"utf8"),size=file.bytes.length,crc=crc32(file.bytes);
    if(name.length>65535||size>0xffffffff||offset>0xffffffff)throw new Error("ZIP32 entry limit exceeded");
    const header=Buffer.alloc(30);header.writeUInt32LE(0x04034b50,0);header.writeUInt16LE(20,4);header.writeUInt16LE(0x800,6);header.writeUInt16LE(0x21,12);header.writeUInt32LE(crc,14);header.writeUInt32LE(size,18);header.writeUInt32LE(size,22);header.writeUInt16LE(name.length,26);
    local.push(header,name,file.bytes);
    const entry=Buffer.alloc(46);entry.writeUInt32LE(0x02014b50,0);entry.writeUInt16LE(20,4);entry.writeUInt16LE(20,6);entry.writeUInt16LE(0x800,8);entry.writeUInt16LE(0x21,14);entry.writeUInt32LE(crc,16);entry.writeUInt32LE(size,20);entry.writeUInt32LE(size,24);entry.writeUInt16LE(name.length,28);entry.writeUInt32LE(offset,42);central.push(entry,name);offset+=header.length+name.length+size;
  }
  const directory=Buffer.concat(central),end=Buffer.alloc(22);end.writeUInt32LE(0x06054b50,0);end.writeUInt16LE(files.length,8);end.writeUInt16LE(files.length,10);end.writeUInt32LE(directory.length,12);end.writeUInt32LE(offset,16);return Buffer.concat([...local,directory,end]);
}
function selfCheck() {
  const folder=fs.mkdtempSync(path.join(os.tmpdir(),"winshot-native-trace-selfcheck-"));
  try{
    fs.mkdirSync(path.join(folder,"src"));fs.writeFileSync(path.join(folder,"manifest.json"),'{"manifest_version":3}');fs.writeFileSync(path.join(folder,"src","a.js"),"console.log('fixture');\n");
    const files=packagedFiles(folder),zip=packageZip(files),checks=[];
    checks.push({name:"explicit nonempty trace categories independent of discovery",pass:TRACE_CATEGORIES.length===8&&TRACE_CATEGORIES.every(x=>typeof x==="string"&&x.length>0)});
    checks.push({name:"trace/capture budgets fit overall hard bound",pass:120000+RESERVE_MS<OVERALL_MS&&TRACE_FLUSH_MS<RESERVE_MS&&MAX_TRACE_BYTES===64*1024*1024&&TRACE_BUFFER_KIB===32768});
    checks.push({name:"CRC32 published check vector",pass:crc32(Buffer.from("123456789"))===0xcbf43926&&crc32(Buffer.alloc(0))===0});
    let cursor=0;const decoded=[];
    while(zip.readUInt32LE(cursor)===0x04034b50){const size=zip.readUInt32LE(cursor+18),nameLength=zip.readUInt16LE(cursor+26),extra=zip.readUInt16LE(cursor+28),name=zip.subarray(cursor+30,cursor+30+nameLength).toString("utf8"),start=cursor+30+nameLength+extra;decoded.push({name,bytes:zip.subarray(start,start+size)});cursor=start+size;}
    checks.push({name:"ZIP exact bytes and portable paths round-trip",pass:decoded.length===files.length&&decoded.every((file,index)=>file.name===files[index].name&&file.bytes.equals(files[index].bytes))&&zip.readUInt32LE(cursor)===0x02014b50&&zip.readUInt32LE(zip.length-22)===0x06054b50});
    checks.push({name:"canonical package hash unaffected by root folder",pass:canonicalBundleHash(files)===canonicalBundleHash(decoded)&&files.every(file=>!file.name.includes("\\"))});
    const result={selfCheckOnly:true,browserLaunched:false,checks,allPass:checks.every(check=>check.pass)};console.log(JSON.stringify(result,null,2));process.exitCode=result.allPass?0:1;
  }finally{fs.rmSync(folder,{recursive:true,force:true});}
}
if(process.argv.includes("--self-check")){selfCheck();process.exit(process.exitCode||0);}

const repository=process.cwd();
const extensionPath=path.resolve(process.env.WINSHOT_NATIVE_TRACE_EXTENSION || path.join(repository,"browser-extension","dist","winshot-capture"));
const output=path.resolve(process.env.WINSHOT_NATIVE_TRACE_OUTPUT || path.join(repository,"output","capture-native-trace"));
const sourceUrl = "https://lenis.dev/";
const startedAt = performance.now();
const deadline = startedAt + OVERALL_MS;
fs.mkdirSync(output, { recursive: true });
fs.copyFileSync(fileURLToPath(import.meta.url),path.join(output,"diagnostic-script.mjs"));
const phasePath = path.join(output, "phases.jsonl");
const apiPath = path.join(output, "worker-api.json");
const resultPath = path.join(output, "diagnostic.json");
const tracePath = path.join(output, "chrome-native-trace.json.gz");
const profile = fs.mkdtempSync(path.join(os.tmpdir(), "winshot-native-trace-"));
const result = {
  diagnosticOnly: true,
  sourceCommit: null,
  githubSha: process.env.GITHUB_SHA || null,
  githubRunId: process.env.GITHUB_RUN_ID || null,
  platform: process.platform,
  claims: { nativeRootCauseProven: false, diagnosticTimingIsComparativeBenchmark: false, sourcePoststateIsFullMediaProof: false },
  startedAt: new Date().toISOString(),
  sourceUrl,
  extensionPath,
  flags: { softwareGL: process.env.WINSHOT_TEST_SOFTWARE_GL, softwareCompositing: process.env.WINSHOT_TEST_SOFTWARE_COMPOSITING },
  scope: "One real-popup capture, unmodified packaged extension; passive event/mutation timestamps and timed native API forwarding. Native trace overhead excludes this sample from performance cohorts."
};
function bundleHash(folder) { return canonicalBundleHash(packagedFiles(folder)); }
function cgroupSnapshot() {
  const read = file => { try { return fs.readFileSync(file, "utf8").trim(); } catch (error) { return String(error); } };
  return { capturedAt: new Date().toISOString(), cpuStat: read("/sys/fs/cgroup/cpu.stat"), cpuMax: read("/sys/fs/cgroup/cpu.max"), cpuPressure: read("/sys/fs/cgroup/cpu.pressure"), memoryEvents: read("/sys/fs/cgroup/memory.events") };
}
result.cgroupBefore = cgroupSnapshot();
try { result.sourceCommit=execFileSync("git",["rev-parse","HEAD"],{cwd:repository,encoding:"utf8",timeout:3000}).trim(); }catch(error){result.sourceProofError=String(error);}
const packageFiles=packagedFiles(extensionPath);
result.bundleSha256Before=canonicalBundleHash(packageFiles);
result.bundleHashFormat="sha256 of sorted POSIX relative path + NUL + raw bytes + NUL";
result.packageFileProof=packageFiles.map(file=>({path:file.name,size:file.bytes.length,sha256:sha256(file.bytes)}));
const archive=packageZip(packageFiles),archivePath=path.join(output,"WinShot-Capture-diagnostic.zip");fs.writeFileSync(archivePath,archive);
result.packageZip={path:archivePath,size:archive.length,sha256:sha256(archive),scope:"Diagnostic archive of exact build directory loaded below; not claimed byte-identical to a separately produced release ZIP."};
result.sourceCommitMatchesGitHubSha=result.githubSha?result.sourceCommit===result.githubSha:null;
result.runtimeSourceProof=["background.js","content.js","page-effects.js"].map(name=>{
  const original=path.join(repository,"browser-extension","src",name),built=path.join(extensionPath,"src",name);
  return {path:"src/"+name,sourceSha256:fs.existsSync(original)?sha256(fs.readFileSync(original)):null,builtSha256:sha256(fs.readFileSync(built)),matches:fs.existsSync(original)&&fs.readFileSync(original).equals(fs.readFileSync(built))};
});
function save() { fs.writeFileSync(resultPath, JSON.stringify(result, null, 2)); }
function limit(promise, milliseconds, label) {
  let timer;
  return Promise.race([promise, new Promise((_,reject)=>{ timer=setTimeout(()=>reject(new Error(label+" exceeded "+milliseconds+" ms")), milliseconds); })]).finally(()=>clearTimeout(timer));
}
let browser, root, worker, workerSession, traceActive=false, traceStopped=false, closing=false, emergency, hardDeadline, traceStartPromise;
let captureController;
const phases=[];
async function saveWorker() {
  if (!worker) return;
  try { result.workerApis = await limit(worker.evaluate(()=>globalThis.__winshotNativeApiTrace || []), 2000, "Worker trace retrieval"); fs.writeFileSync(apiPath, JSON.stringify(result.workerApis, null, 2)); }
  catch(error) { result.workerTraceError=String(error); }
}
async function startNativeTrace(trigger) {
  result.traceTrigger=trigger;
  result.traceStartedNodeAt=performance.now();
  // Category discovery is advisory. Empty discovery must NEVER become an empty
  // allow-list (which means broad/default recording in this Chrome build).
  const includedCategories=[...TRACE_CATEGORIES];
  result.traceCategories=includedCategories;
  result.traceConfig={recordMode:"recordContinuously",traceBufferSizeInKb:TRACE_BUFFER_KIB,enableSampling:false,enableSystrace:false,includedCategories};
  await limit(root.send("Tracing.start",{
    transferMode:"ReturnAsStream",streamFormat:"json",streamCompression:"gzip",bufferUsageReportingInterval:1000,
    traceConfig:result.traceConfig
  }),5000,"Native trace start");
  traceActive=true;
  result.traceStartAcknowledged=true;
  await root.send("Tracing.recordClockSyncMarker",{syncId:"WINSHOT_CORRECTED_TRACE_ARM"});
  process.stdout.write(JSON.stringify({traceStarted:true,trigger,categories:includedCategories})+"\n");
}
async function stopTrace(reason) {
  if (traceStartPromise) await limit(traceStartPromise,5000,"Pending native trace start").catch(error=>{result.traceStartError=String(error);});
  if (!root || !traceActive || traceStopped) return;
  traceStopped=true;
  result.traceStopReason=reason;
  const stopDeadline=Math.min(performance.now()+TRACE_FLUSH_MS,deadline-5000);
  const done = new Promise(resolve=>root.once("Tracing.tracingComplete", resolve));
  try {
    await limit(root.send("Tracing.end"),3000,"Tracing.end");
    const completed=await limit(done,Math.max(1,Math.min(33000,stopDeadline-performance.now()-5000)),"Native trace flush");
    result.traceComplete=completed;
    if (!completed.stream) throw new Error("Tracing did not return a stream");
    const handle=completed.stream,partialPath=tracePath+".partial",fd=fs.openSync(partialPath,"w");
    let bytes=0,eof=false;
    try {
      while (performance.now()<stopDeadline) {
        const chunk=await limit(root.send("IO.read",{handle,size:1024*1024}),Math.max(1,Math.min(3000,stopDeadline-performance.now())),"Trace stream read");
        const data=Buffer.from(chunk.data,chunk.base64Encoded?"base64":"utf8");
        fs.writeSync(fd,data); bytes+=data.length;
        if(chunk.eof){eof=true;break;}
        if(bytes>=MAX_TRACE_BYTES){result.traceStreamByteLimitReached=true;break;}
      }
    } finally { fs.closeSync(fd); await limit(root.send("IO.close",{handle}),1000,"Trace stream close").catch(()=>{}); }
    result.traceBytes=bytes;
    result.traceStreamTruncated=!eof;
    if(eof){fs.renameSync(partialPath,tracePath);result.tracePath=tracePath;result.traceSha256=crypto.createHash("sha256").update(fs.readFileSync(tracePath)).digest("hex");}
    else{result.tracePartialPath=partialPath;throw new Error("Native trace stream incomplete before byte/time budget");}
  } catch(error) { result.traceError=String(error); }
}
async function closeOwned() {
  if (closing) return; closing=true;
  const force=setTimeout(()=>browser?.process()?.kill("SIGKILL"),3000);
  try { await limit(browser?.close() || Promise.resolve(),5000,"Owned browser close"); } catch {}
  clearTimeout(force);
}
hardDeadline=setTimeout(()=>{
  result.interrupted=true;result.interruptionReason="180 s hard diagnostic deadline";
  result.phases=phases;result.totalMs=performance.now()-startedAt;save();
  browser?.process()?.kill("SIGKILL");process.exit(2);
},179000);
emergency=setTimeout(async()=>{
  result.interrupted=true; result.interruptionReason="175 s safety shutdown";
  captureController?.abort();save();await closeOwned();
},175000);
try {
  const proxy=process.env.HTTPS_PROXY || process.env.HTTP_PROXY;
  browser=await launchCaptureBrowser(profile,{width:1200,height:1000},{args:proxy?["--proxy-server="+proxy]:[]});
  result.browserVersion=await browser.version();
  result.browserArguments=browser.process()?.spawnargs.filter(value=>/gpu|angle|gl=|swiftshader|headless|window-size/.test(value));
  const id=await browser.installExtension(extensionPath);
  result.extensionId=id;
  result.manifestVersion=JSON.parse(fs.readFileSync(path.join(extensionPath,"manifest.json"),"utf8")).version;
  const workerTarget=await browser.waitForTarget(target=>target.type()==="service_worker"&&target.url().endsWith("/src/background.js"));
  worker=await workerTarget.worker();
  workerSession=await workerTarget.createCDPSession();
  await workerSession.send("Runtime.enable");
  workerSession.on("Runtime.consoleAPICalled",event=>{
    const value=event.args?.[0]?.value,prefix="__WINSHOT_NATIVE_TRACE_ARM__ ";
    if(typeof value!=="string" || !value.startsWith(prefix) || traceStartPromise)return;
    try{result.traceArmSignal=JSON.parse(value.slice(prefix.length));}catch{result.traceArmSignal={invalid:true};}
    traceStartPromise=startNativeTrace(result.traceArmSignal).catch(error=>{result.traceStartError=String(error);});
  });
  const page=await browser.newPage();
  page.on("console", message=>{
    const prefix="__WINSHOT_NATIVE_TRACE__ ";
    if (!message.text().startsWith(prefix)) return;
    try {
      const entry={...JSON.parse(message.text().slice(prefix.length)),observedAt:performance.now()};
      phases.push(entry); fs.appendFileSync(phasePath,JSON.stringify(entry)+"\n");
      if (/restore|CSS|snapshot/.test(entry.label)) process.stdout.write(JSON.stringify({phase:entry})+"\n");
    } catch {}
  });
  page.on("pageerror", error=>{ (result.sourceErrors ||= []).push(error.message); });
  await page.setCacheEnabled(false);
  result.navigationAttempted=true;
  const response=await page.goto(sourceUrl,{waitUntil:"domcontentloaded",timeout:30000});
  result.navigationStatus=response?.status()??null;
  if (!response?.ok() && response?.status()!==304) {result.classification="source-unavailable-before-capture";throw new Error("Source returned HTTP "+response?.status());}
  await page.evaluate(()=>Promise.race([document.fonts.ready,new Promise(resolve=>setTimeout(resolve,2500))]));
  await new Promise(resolve=>setTimeout(resolve,1000));
  await page.evaluate(()=>scrollTo(0,230));
  result.source=await page.evaluate(()=>({x:scrollX,y:scrollY,width:document.scrollingElement.scrollWidth,height:document.scrollingElement.scrollHeight,canvas:document.querySelectorAll("canvas").length,media:document.querySelectorAll("video,audio").length,timeOrigin:performance.timeOrigin}));
  root=await browser.target().createCDPSession();
  try { result.discoveredNativeCategories=(await root.send("Tracing.getCategories")).categories; }
  catch(error){result.categoryDiscoveryError=String(error);}
  root.on("Tracing.bufferUsage", usage=>{result.lastBufferUsage=usage;});
  await worker.evaluate(()=>{
    globalThis.__winshotNativeApiTrace=[];
    const nativeInfo=console.info.bind(console);
    let armed=false;
    for (const key of ["captureVisibleTab","sendMessage"]) {
      const native=chrome.tabs[key];
      chrome.tabs[key]=async function(...args) {
        const type=key==="sendMessage"?args[1]?.type:null;
        const request=args[1];
        if(!armed && (type==="WINSHOT_RESTORE" || (type==="WINSHOT_SCROLL" && request.viewport?.height>0 && request.extent?.height>0 &&
            request.y>=request.extent.height-2*request.viewport.height && request.x>=request.extent.width-request.viewport.width-1))){
          armed=true;
          nativeInfo("__WINSHOT_NATIVE_TRACE_ARM__ "+JSON.stringify({type,y:request.y,height:request.extent?.height,viewportHeight:request.viewport?.height,at:performance.now(),timeOrigin:performance.timeOrigin}));
        }
        const item={api:key,type,x:args[1]?.x,y:args[1]?.y,start:performance.now(),timeOrigin:performance.timeOrigin};
        globalThis.__winshotNativeApiTrace.push(item);
        if (type || key==="captureVisibleTab") console.timeStamp("WS API "+(type||key)+" begin");
        try { return await Reflect.apply(native,this,args); }
        catch(error) { item.error=String(error); throw error; }
        finally { item.end=performance.now();item.durationMs=item.end-item.start;if(type || key==="captureVisibleTab")console.timeStamp("WS API "+(type||key)+" end"); }
      };
    }
  });
  const [{id:tabId}]=await worker.evaluate(async url=>chrome.tabs.query({url}),page.url());
  const relay="__winshotNativeTraceRelay";
  await worker.evaluate(async ({tabId,relay})=>chrome.scripting.executeScript({
    target:{tabId},world:"ISOLATED",args:[relay],
    func:relay=>{
      const nativeStamp=console.timeStamp.bind(console),nativeInfo=console.info.bind(console);
      const emit=(label,world="ISOLATED")=>{
        nativeStamp(label);
        nativeInfo("__WINSHOT_NATIVE_TRACE__",JSON.stringify({label,world,at:performance.now(),timeOrigin:performance.timeOrigin}));
      };
      addEventListener(relay,event=>{if(typeof event.detail==="string"){try {const data=JSON.parse(event.detail);emit(data.label,"MAIN");}catch{}}});
      const actions=new Map();
      addEventListener("message",event=>{
        const data=event.data;
        if(event.source!==window || !/^winshot-effects-(request|response)-v3$/.test(data?.source||""))return;
        if(data.action)actions.set(data.nonce,data.action);
        const action=data.action||actions.get(data.nonce)||"unknown";
        emit("WS ISOLATED "+(data.action?"request ":"response ")+action);
        if(!data.action)actions.delete(data.nonce);
      });
      chrome.runtime.onMessage.addListener(message=>{
        if (["WINSHOT_PREPARE","WINSHOT_SCROLL","WINSHOT_RESTORE"].includes(message.type)) emit("WS content received "+message.type);
        return false;
      });
      new MutationObserver(records=>{
        for(const record of records)for(const node of record.removedNodes){
          if(node instanceof HTMLStyleElement&&node.hasAttribute("data-winshot-capture"))emit("WS capture CSS removed");
          if(node instanceof HTMLCanvasElement&&node.hasAttribute("data-winshot-surface"))emit("WS canvas snapshot removed");
        }
      }).observe(document.documentElement,{childList:true,subtree:true});
      emit("WS ISOLATED diagnostic ready");
    }
  }),{tabId,relay});
  await worker.evaluate(async ({tabId,relay})=>chrome.scripting.executeScript({
    target:{tabId},world:"MAIN",args:[relay],
    func:relay=>{
      const emit=label=>dispatchEvent(new CustomEvent(relay,{detail:JSON.stringify({label})}));
      const actions=new Map();
      addEventListener("message",event=>{
        const data=event.data;
        if(event.source!==window || !/^winshot-effects-(request|response)-v3$/.test(data?.source||""))return;
        if(data.action)actions.set(data.nonce,data.action);
        const action=data.action||actions.get(data.nonce)||"unknown";
        emit("WS MAIN "+(data.action?"request ":"response ")+action);
        if(!data.action)actions.delete(data.nonce);
      });
      emit("WS MAIN diagnostic ready");
    }
  }),{tabId,relay});
  await page.bringToFront();
  const before=new Set(browser.targets());
  const popupWait=browser.waitForTarget(target=>!before.has(target)&&target.type()==="page"&&target.url().endsWith("/src/popup.html"),{timeout:10000});
  await (await browser.extensions()).get(id).triggerAction(page);
  const popup=await (await popupWait).asPage();
  await popup.waitForSelector("body[data-ready='true']",{timeout:10000});
  await popup.evaluate(()=>{document.querySelector("#capture-height").value="80000";document.querySelector("#capture-timeout").value="2";document.querySelector("#capture-tiles").value="120";});
  captureController=new AbortController();
  const captureBudget=Math.max(1000,Math.min(120000,deadline-performance.now()-RESERVE_MS));
  result.captureBudgetMs=captureBudget;
  const targetWait=browser.waitForTarget(target=>!before.has(target)&&target.type()==="page"&&target.url().includes("/src/editor.html?id="),{timeout:captureBudget,signal:captureController.signal});
  targetWait.catch(()=>{});
  const captureStarted=performance.now();
  result.captureStartedAt=captureStarted;
  result.captureAttempted=true;
  await popup.click("[data-mode='full-page']");
  const editor=await (await targetWait).asPage();
  result.editorOpenedMs=performance.now()-captureStarted;
  const remaining=()=>Math.max(1,Math.min(60000,deadline-performance.now()-45000));
  await editor.waitForFunction(()=>{
    const state=document.querySelector("#state")?.textContent.trim()||"";
    const images=[...document.querySelectorAll("#preview img")],overlay=document.querySelector("#preview .annotation-overlay"),box=overlay?.getBoundingClientRect();
    return state&&!/^loading\b/i.test(state)&&images.length>0&&images.every(image=>image.complete&&image.naturalWidth>0)&&(!document.querySelector("[data-tool]")||Boolean(box?.width&&box?.height));
  },{timeout:remaining()});
  await limit(editor.evaluate(()=>Promise.all([...document.querySelectorAll("#preview img")].map(image=>image.decode()))),remaining(),"Preview decode");
  result.decodedEditorReadyMs=performance.now()-captureStarted;
  result.record=await limit(editor.evaluate(async()=>{const store=await import(chrome.runtime.getURL("src/store.js"));return store.getRecord(new URL(location.href).searchParams.get("id"));}),2000,"Record retrieval");
  await saveWorker();
  try { result.sourcePoststate=await limit(page.evaluate(()=>({x:scrollX,y:scrollY,captureStyles:document.querySelectorAll("style[data-winshot-capture]").length,snapshots:document.querySelectorAll("canvas[data-winshot-surface]").length,play:Function.prototype.toString.call(HTMLMediaElement.prototype.play),pause:Function.prototype.toString.call(HTMLMediaElement.prototype.pause)})),2000,"Source poststate"); }
  catch(error){result.sourcePoststateError=String(error);}
  result.completed=true;
  result.captureReachedDecodedEditor=true;
  result.restorationVerified=result.record?.report?.restoration==="verified by read-back";
  result.classification=result.restorationVerified?"capture-completed-restoration-acknowledged":"capture-completed-restoration-partial";
} catch(error) {
  result.error=String(error); result.completed=false;
  result.classification ||= result.captureAttempted?"capture-diagnostic-failed-or-timed-out":"setup-or-network-unavailable-before-capture";
} finally {
  captureController?.abort();
  await saveWorker();
  await stopTrace(result.completed?"decoded editor ready":"diagnostic failed or timed out");
  result.phases=phases;
  result.bundleSha256After=bundleHash(extensionPath);
  result.packagedFilesUnchanged=result.bundleSha256Before===result.bundleSha256After;
  result.cgroupAfter = cgroupSnapshot();
  result.finishedAt=new Date().toISOString();
  result.totalMs=performance.now()-startedAt;
  save();
  await closeOwned();
  clearTimeout(emergency);
  clearTimeout(hardDeadline);
  if(result.tracePath&&!result.traceStreamTruncated){
    try{
      const trace=JSON.parse(zlib.gunzipSync(fs.readFileSync(result.tracePath),{maxOutputLength:256*1024*1024}).toString("utf8"));
      const events=trace.traceEvents||[],processes={},threads={},markers=[];
      for(const event of events){
        if(event.ph==="M"&&event.name==="process_name")processes[event.pid]=event.args?.name;
        if(event.ph==="M"&&event.name==="thread_name")threads[event.pid+":"+event.tid]=event.args?.name;
        const label=event.args?.data?.message;
        if(typeof label==="string"&&label.startsWith("WS "))markers.push({atUs:event.ts,pid:event.pid,tid:event.tid,label});
      }
      markers.sort((a,b)=>a.atUs-b.atUs);
      const begin=markers.find(item=>item.label==="WS API WINSHOT_RESTORE begin")?.atUs;
      const end=markers.find(item=>item.label==="WS API WINSHOT_RESTORE end"&&(!begin||item.atUs>=begin))?.atUs;
      const longest=begin===undefined?[]:events.filter(event=>event.ph==="X"&&event.dur>=10000&&event.ts<(end??Infinity)&&event.ts+event.dur>begin).sort((a,b)=>b.dur-a.dur).slice(0,80).map(event=>({startRelativeMs:(event.ts-begin)/1000,durationMs:event.dur/1000,name:event.name,category:event.cat,pid:event.pid,tid:event.tid,process:processes[event.pid],thread:threads[event.pid+":"+event.tid],args:JSON.stringify(event.args||{}).slice(0,2000)}));
      const summary={nativeRootCauseProven:false,scope:"Native trace task wall durations can overlap; they are not additive CPU time or automatic root-cause attribution.",eventCount:events.length,tracingComplete:result.traceComplete,dataLossStatus:typeof result.traceComplete?.dataLossOccurred==="boolean"?result.traceComplete.dataLossOccurred:"unknown",processes,threads,markers,longestTasksOverlappingRestore:longest};
      fs.writeFileSync(path.join(output,"native-timeline-summary.json"),JSON.stringify(summary,null,2));
      result.traceValidation={gzipAndJsonParsed:true,eventCount:events.length,markerCount:markers.length,dataLossStatus:summary.dataLossStatus,restoreBeginObserved:begin!==undefined,restoreEndObserved:end!==undefined,captureCssRemovalObserved:markers.some(marker=>marker.label==="WS capture CSS removed")};
    }catch(error){result.traceValidationError=String(error);}
  }
  result.totalIncludingTraceAnalysisMs=performance.now()-startedAt;save();
  if(!result.completed||!result.traceValidation?.gzipAndJsonParsed)process.exitCode=1;
  fs.rmSync(profile,{recursive:true,force:true,maxRetries:3,retryDelay:100});
  process.stdout.write(JSON.stringify({completed:result.completed,error:result.error,editorOpenedMs:result.editorOpenedMs,decodedEditorReadyMs:result.decodedEditorReadyMs,restoration:result.record?.report?.restoration,tracePath:result.tracePath,traceBytes:result.traceBytes,traceError:result.traceError,totalMs:result.totalMs})+"\n");
}
