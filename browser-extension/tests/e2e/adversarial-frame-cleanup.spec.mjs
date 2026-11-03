import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {test,expect} from '@playwright/test';
import {launchCaptureBrowser} from './launch-browser.mjs';

test('a failed nested frame location keeps its original error while delayed native parent cleanup is acknowledged',async({},testInfo)=>{
  const profile=fs.mkdtempSync(path.join(os.tmpdir(),'winshot-frame-cleanup-'));
  const browser=await launchCaptureBrowser(profile);
  let worker,before,after,outcome,record,followUp;
  try {
    const id=await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH||fileURLToPath(new URL('../../dist/winshot-capture',import.meta.url)));
    const page=await browser.newPage();await page.goto('http://127.0.0.1:4173/iframes',{waitUntil:'networkidle0'});
    const host=page.frames().find(frame=>frame.url()==='http://127.0.0.1:4173/nested-frame-host');
    const source=page.frames().find(frame=>frame.parentFrame()===host&&frame.url()==='http://127.0.0.1:4173/frame');
    expect(source).toBeTruthy();
    // The second parent has a real child which cannot be fitted in its viewport.
    // Its native LOCATE error occurs after the top parent has been prepared.
    await host.$eval('iframe',frame=>{frame.style.cssText='position:fixed;left:-2000px;top:0;width:900px;height:300px;border:4px solid #f4bd00';});
    await source.evaluate(()=>scrollTo(0,360));await page.evaluate(()=>scrollTo(0,120));
    const snapshot=async frame=>frame.evaluate(()=>{
      window.frameCleanupOriginalMedia||={play:HTMLMediaElement.prototype.play,pause:HTMLMediaElement.prototype.pause};
      return {x:scrollX,y:scrollY,htmlStyle:document.documentElement.style.cssText,bodyStyle:document.body.style.cssText,frameStyles:[...document.querySelectorAll('iframe')].map(frame=>frame.style.cssText),captureStyles:document.querySelectorAll('style[data-winshot-capture]').length,playOriginal:HTMLMediaElement.prototype.play===window.frameCleanupOriginalMedia.play,pauseOriginal:HTMLMediaElement.prototype.pause===window.frameCleanupOriginalMedia.pause};
    });
    before=await Promise.all([snapshot(page.mainFrame()),snapshot(host),snapshot(source)]);
    worker=await(await browser.waitForTarget(target=>target.type()==='service_worker')).worker();
    const [tab]=await worker.evaluate(async url=>chrome.tabs.query({url}),page.url());
    await worker.evaluate(()=>{
      globalThis.frameCleanupTrace=[];
      const send=chrome.tabs.sendMessage;
      chrome.tabs.sendMessage=async function(...args){
        const row={type:args[1]?.type,frameId:args[2]?.frameId,at:performance.now()};frameCleanupTrace.push(row);
        try {
          const reply=await Reflect.apply(send,this,args);row.nativeReturnedAt=performance.now();row.reply=reply;
          if(row.type==='WINSHOT_RESTORE_FRAME_HOST'&&reply?.restored&&!globalThis.frameCleanupDelayedRestore){
            globalThis.frameCleanupDelayedRestore=true;row.successfulNativeAcknowledgementDelayMs=3000;
            await new Promise(resolve=>setTimeout(resolve,3000));
          }
          return reply;
        }finally{row.end=performance.now();}
      };
    });
    outcome=await worker.evaluate(async tab=>{
      const frames=await chrome.webNavigation.getAllFrames({tabId:tab.id});
      const host=frames.find(frame=>frame.url==='http://127.0.0.1:4173/nested-frame-host');
      const source=frames.find(frame=>frame.parentFrameId===host.frameId&&frame.url==='http://127.0.0.1:4173/frame');
      return globalThis.__winshotTest.runCapture('full-page',{frameId:source.frameId,frameUrl:source.url},tab,{openEditor:false});
    },tab);
    after=await Promise.all([snapshot(page.mainFrame()),snapshot(host),snapshot(source)]);
    const storePage=await browser.newPage();await storePage.goto(`chrome-extension://${id}/src/popup.html`);
    record=await storePage.evaluate(async captureId=>{const {getRecord}=await import(chrome.runtime.getURL('src/store.js'));return getRecord(captureId);},outcome.captureId);
    const trace=await worker.evaluate(()=>frameCleanupTrace);
    const locate=trace.filter(row=>row.type==='WINSHOT_LOCATE_FRAME');
    expect(locate).toHaveLength(2);expect(locate[0].reply.error).toBeUndefined();
    expect(locate[1].reply.error).toContain('frame viewport could not be fitted');
    expect(outcome.state).toBe('failed');expect(outcome.error).toBe(locate[1].reply.error);
    expect(record.report.warnings.join(' ')).not.toContain('capture session expired');
    expect(record.report.restoration).toBe('verified by read-back');
    expect(trace.filter(row=>row.type==='WINSHOT_RESTORE_FRAME_HOST')).toHaveLength(1);
    expect(trace.filter(row=>row.type==='WINSHOT_HEARTBEAT_FRAME_HOST'&&row.reply?.alive===false)).toHaveLength(0);
    expect(trace.find(row=>row.successfulNativeAcknowledgementDelayMs)?.reply).toEqual({restored:true});
    expect(after).toEqual(before);
    await page.bringToFront();
    const next=await worker.evaluate(tab=>globalThis.__winshotTest.runCapture('visible',null,tab,{openEditor:false}),tab);
    followUp=await storePage.evaluate(async captureId=>{
      const {getRecord,getTiles}=await import(chrome.runtime.getURL('src/store.js'));
      const record=await getRecord(captureId),tiles=await getTiles(captureId),bitmap=await createImageBitmap(tiles[0].blob);
      const context=new OffscreenCanvas(32,32).getContext('2d');context.drawImage(bitmap,0,0,32,32);
      const data=context.getImageData(0,0,32,32).data;let min=255,max=0;
      for(let i=0;i<data.length;i+=4){min=Math.min(min,data[i],data[i+1],data[i+2]);max=Math.max(max,data[i],data[i+1],data[i+2]);}
      const result={state:record.state,tileCount:record.report.tileCount,width:bitmap.width,height:bitmap.height,nonblank:max-min>10};bitmap.close();return result;
    },next.captureId);
    expect(followUp).toEqual({state:'complete',tileCount:1,width:1200,height:1000,nonblank:true});
  }finally{
    if(worker)await testInfo.attach('native-frame-cleanup-proof',{body:JSON.stringify({before,after,outcome,record,followUp,trace:await worker.evaluate(()=>globalThis.frameCleanupTrace)}),contentType:'application/json'});
    await browser.close();fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});
  }
});
