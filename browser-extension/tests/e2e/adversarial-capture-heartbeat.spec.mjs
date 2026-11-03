import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { launchCaptureBrowser } from './launch-browser.mjs';

async function launch(delayMs=0) {
  const profile=fs.mkdtempSync(path.join(os.tmpdir(),'winshot-heartbeat-'));
  const browser=await launchCaptureBrowser(profile);
  try {
    const id=await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH||fileURLToPath(new URL('../../dist/winshot-capture',import.meta.url)));
    const extension=(await browser.extensions()).get(id),page=await browser.newPage();
    await page.goto('http://127.0.0.1:4173/body',{waitUntil:'networkidle0'});
    await page.evaluate(()=>{document.body.innerHTML='<div style="height:4400px;background:repeating-linear-gradient(#fff 0 50px,#ddd 50px 100px)"><div style="position:relative;top:4200px;height:200px;background:#ff00cc">LAST PIXELS</div></div>';scrollTo(0,230);window.savedHeartbeatMedia={play:HTMLMediaElement.prototype.play,pause:HTMLMediaElement.prototype.pause};});
    const worker=await(await browser.waitForTarget(t=>t.type()==='service_worker')).worker();
    const [tab]=await worker.evaluate(async url=>chrome.tabs.query({url}),page.url());
    await worker.evaluate(delayMs=>{
      globalThis.heartbeatTrace=[];globalThis.heartbeatNativeCalls=0;
      const send=chrome.tabs.sendMessage;
      chrome.tabs.sendMessage=async function(...args){const row={type:args[1]?.type,sessionId:args[1]?.sessionId,at:performance.now()};heartbeatTrace.push(row);try{return row.reply=await Reflect.apply(send,this,args);}finally{row.end=performance.now();}};
      const capture=chrome.tabs.captureVisibleTab;
      chrome.tabs.captureVisibleTab=async function(...args){
        const pixels=await Reflect.apply(capture,this,args);
        if(++globalThis.heartbeatNativeCalls===3&&delayMs){globalThis.heartbeatDelayStarted=performance.now();await new Promise(resolve=>setTimeout(resolve,delayMs));globalThis.heartbeatDelayEnded=performance.now();}
        return pixels;
      };
    },delayMs);
    return {browser,extension,page,worker,tab,async close(){await browser.close();fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});}};
  }catch(error){await browser.close();throw error;}
}
async function startPopup(run) {
  await run.page.bringToFront();
  const pt=run.browser.waitForTarget(t=>t.type()==='page'&&t.url().endsWith('/src/popup.html'));
  const editor=run.browser.waitForTarget(t=>t.type()==='page'&&t.url().includes('/src/editor.html?id='),{timeout:60000});
  await run.extension.triggerAction(run.page);const popup=await(await pt).asPage();
  await popup.waitForSelector("body[data-ready='true']");await popup.click("[data-mode='full-page']");
  return {popup,editor};
}
async function poststate(run) {
  return run.page.evaluate(()=>({styles:document.querySelectorAll('style[data-winshot-capture]').length,scroll:scrollY,playRestored:HTMLMediaElement.prototype.play===window.savedHeartbeatMedia.play,pauseRestored:HTMLMediaElement.prototype.pause===window.savedHeartbeatMedia.pause}));
}
async function attach(run,testInfo) {await testInfo.attach('native-heartbeat-trace',{body:JSON.stringify(await run.worker.evaluate(()=>({trace:heartbeatTrace,delayStarted:globalThis.heartbeatDelayStarted,delayEnded:globalThis.heartbeatDelayEnded}))),contentType:'application/json'});}

test('a healthy native screenshot callback delayed beyond the watchdog keeps both capture worlds alive',async({},testInfo)=>{
  const run=await launch(12000);
  try {
    const {editor}=await startPopup(run);
    await expect.poll(()=>run.worker.evaluate(()=>Boolean(globalThis.heartbeatDelayStarted))).toBe(true);
    await new Promise(resolve=>setTimeout(resolve,10500));
    expect(await poststate(run)).toEqual({styles:1,scroll:936,playRestored:false,pauseRestored:false});
    const ui=await(await editor).asPage();await ui.waitForSelector('#preview img');
    const record=await ui.evaluate(async()=>{const s=await import(chrome.runtime.getURL('src/store.js'));return s.getRecord(new URL(location.href).searchParams.get('id'));});
    expect(record.state,JSON.stringify(record.report)).toBe('complete');expect(record.report.coverage.complete).toBe(true);expect(record.report.dimensionsPx.height).toBe(4400);expect(record.report.restoration).toBe('verified by read-back');
    expect(await poststate(run)).toEqual({styles:0,scroll:230,playRestored:true,pauseRestored:true});await attach(run,testInfo);
  }finally{await run.close();}
});

test('Stop during a delayed native callback stops renewals and preserves watchdog cleanup',async({},testInfo)=>{
  const run=await launch(14000);
  try {
    const {popup,editor}=await startPopup(run);editor.catch(()=>{});
    await expect.poll(()=>run.worker.evaluate(()=>Boolean(globalThis.heartbeatDelayStarted))).toBe(true);
    await run.worker.evaluate(async()=>{globalThis.heartbeatStoppedAt=performance.now();await globalThis.__winshotTest.cancelActiveCapture({global:true});});
    await run.page.waitForFunction(()=>!document.querySelector('style[data-winshot-capture]')&&HTMLMediaElement.prototype.play===window.savedHeartbeatMedia.play&&HTMLMediaElement.prototype.pause===window.savedHeartbeatMedia.pause,{timeout:11000});
    expect(await poststate(run)).toEqual({styles:0,scroll:230,playRestored:true,pauseRestored:true});
    expect(await run.worker.evaluate(()=>heartbeatTrace.filter(row=>row.type==='WINSHOT_HEARTBEAT'&&row.at>heartbeatStoppedAt+1).length)).toBe(0);
    await expect.poll(()=>popup.$eval('body',node=>node.dataset.busy),{timeout:10000}).toBe('false');await attach(run,testInfo);
  }finally{await run.close();}
});

test('capture deadline stops heartbeat renewal without extending the configured duration',async({},testInfo)=>{
  const run=await launch(14000);
  try {
    await run.worker.evaluate(tab=>{globalThis.heartbeatAttemptStarted=performance.now();globalThis.heartbeatCapture=globalThis.__winshotTest.runCapture('full-page',null,tab,{openEditor:false,limits:{maxDurationMs:4000}});},run.tab);
    await expect.poll(()=>run.worker.evaluate(()=>Boolean(globalThis.heartbeatDelayStarted))).toBe(true);
    await new Promise(resolve=>setTimeout(resolve,5000));
    expect(await run.worker.evaluate(()=>heartbeatTrace.filter(row=>row.type==='WINSHOT_HEARTBEAT'&&row.at>heartbeatAttemptStarted+4500).length)).toBe(0);
    const outcome=await run.worker.evaluate(()=>globalThis.heartbeatCapture);
    const storePage=await run.browser.newPage();await storePage.goto(`chrome-extension://${run.extension.id}/src/popup.html`);
    const record=await storePage.evaluate(async id=>{const {getRecord}=await import(chrome.runtime.getURL('src/store.js'));return getRecord(id);},outcome.captureId);
    expect(record.state).toBe('partial');expect(record.limits.maxDurationMs).toBe(4000);expect(record.report.warnings.join(' ')).toContain('configured time limit');expect(record.report.coverage.complete).toBe(false);
    expect(await poststate(run)).toEqual({styles:0,scroll:230,playRestored:true,pauseRestored:true});await attach(run,testInfo);
  }finally{await run.close();}
});

test('heartbeats reject expired and wrong sessions and distinguish missing MAIN acknowledgement',async()=>{
  const run=await launch();
  try {
    await run.worker.evaluate(async tabId=>{await chrome.scripting.executeScript({target:{tabId},world:'MAIN',files:['src/page-effects.js']});await chrome.scripting.executeScript({target:{tabId},files:['src/content.js']});},run.tab.id);
    const send=message=>run.worker.evaluate(({tabId,message})=>chrome.tabs.sendMessage(tabId,message),{tabId:run.tab.id,message});
    await send({type:'WINSHOT_PREPARE',sessionId:'first',mode:'full-page'});
    expect(await send({type:'WINSHOT_HEARTBEAT',sessionId:'wrong'})).toEqual({alive:false,effectsAlive:false});
    expect(await send({type:'WINSHOT_HEARTBEAT',sessionId:'first'})).toEqual({alive:true,effectsAlive:true});
    await run.page.evaluate(()=>postMessage({source:'winshot-effects-request-v4',action:'restore',id:'first',nonce:'test-expire-main'},'*'));
    await run.page.waitForFunction(()=>HTMLMediaElement.prototype.play===window.savedHeartbeatMedia.play);
    expect(await send({type:'WINSHOT_HEARTBEAT',sessionId:'first'})).toEqual({alive:true,effectsAlive:false});
    await send({type:'WINSHOT_RESTORE',sessionId:'first'});
    expect(await send({type:'WINSHOT_HEARTBEAT',sessionId:'first'})).toEqual({alive:false,effectsAlive:false});
    expect(await poststate(run)).toEqual({styles:0,scroll:230,playRestored:true,pauseRestored:true});
    await send({type:'WINSHOT_PREPARE',sessionId:'second',mode:'full-page'});
    await run.page.evaluate(()=>window[Symbol.for('winshot.page-effects')].dispose());
    expect(await send({type:'WINSHOT_HEARTBEAT',sessionId:'second'})).toEqual({alive:true,effectsAlive:null});
    await send({type:'WINSHOT_RESTORE',sessionId:'second'});
  }finally{await run.close();}
});
