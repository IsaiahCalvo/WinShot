import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '@playwright/test';
import { getDocument } from 'pdfjs-dist/legacy/build/pdf.mjs';
import { launchCaptureBrowser } from './launch-browser.mjs';

async function runCapture(setup, verify) {
  const profile=fs.mkdtempSync(path.join(os.tmpdir(),'winshot-media-output-'));
  const browser=await launchCaptureBrowser(profile);
  try {
    const id=await browser.installExtension(process.env.WINSHOT_EXTENSION_PATH || fileURLToPath(new URL('../../dist/winshot-capture',import.meta.url)));
    const extension=(await browser.extensions()).get(id), page=await browser.newPage();
    await page.goto('http://127.0.0.1:4173/sticky');
    if(setup) await setup(page);
    const target=browser.waitForTarget(t=>t.type()==='page'&&t.url().includes('/src/editor.html?id='),{timeout:60000});
    const pt=browser.waitForTarget(t=>t.type()==='page'&&t.url().endsWith('/src/popup.html'));
    await extension.triggerAction(page);const popup=await(await pt).asPage();
    await popup.waitForSelector("body[data-ready='true']");await popup.click("[data-mode='full-page']");
    const editor=await(await target).asPage();await editor.waitForSelector('#preview img');
    const record=await editor.evaluate(async()=>{const s=await import(chrome.runtime.getURL('src/store.js'));return s.getRecord(new URL(location.href).searchParams.get('id'));});
    expect(record.state,JSON.stringify(record.report)).toBe('complete');
    await verify({browser,id,page,editor,record});
  } finally {await browser.close();fs.rmSync(profile,{recursive:true,force:true,maxRetries:5,retryDelay:100});}
}

test('videos pause during capture, restore playback, and respect a user-paused video',async()=>{
  await runCapture(async page=>page.evaluate(async()=>{
    document.body.innerHTML='<div style="height:3500px;background:linear-gradient(white,#ccc)"><video id="playing" muted loop width="180" height="100"></video><video id="paused" muted loop width="180" height="100"></video><video id="requested" muted loop width="180" height="100"></video></div>';
    const canvas=document.createElement('canvas');canvas.width=180;canvas.height=100;
    const ctx=canvas.getContext('2d'),stream=canvas.captureStream(30),recorder=new MediaRecorder(stream,{mimeType:'video/webm;codecs=vp8'}),parts=[];
    recorder.ondataavailable=e=>parts.push(e.data);const stopped=new Promise(r=>recorder.onstop=r);recorder.start();
    for(let i=0;i<30;i++){ctx.fillStyle=i%2?'#04c870':'#0088ff';ctx.fillRect(0,0,180,100);await new Promise(r=>setTimeout(r,35));}
    recorder.stop();await stopped;stream.getTracks().forEach(t=>t.stop());
    const url=URL.createObjectURL(new Blob(parts,{type:'video/webm'}));
    await Promise.all([...document.querySelectorAll('video')].map(async v=>{v.src=url;v.muted=true;await v.play();v.pause();v.currentTime=.2;}));
    window.originalPlay=HTMLMediaElement.prototype.play;window.trace=[];window.didRequest=false;
    addEventListener('scroll',()=>{if(scrollY>500&&!window.didRequest){window.didRequest=true;document.querySelector('#requested').play();}window.trace.push({y:scrollY,paused:[...document.querySelectorAll('video')].map(v=>v.paused)});});
    await document.querySelector('#playing').play();
  }),async({page,record})=>{
    await page.bringToFront();
    const after=await page.evaluate(()=>({videos:[...document.querySelectorAll('video')].map(v=>({paused:v.paused,time:v.currentTime})),prototypeRestored:HTMLMediaElement.prototype.play===window.originalPlay,trace:window.trace}));
    expect(after.prototypeRestored).toBe(true);expect(after.videos.map(v=>v.paused)).toEqual([false,true,false]);
    expect(after.videos[1].time).toBeCloseTo(.2,2);
    expect(after.trace.filter(v=>v.y>0).length).toBeGreaterThan(1);
    expect(after.trace.filter(v=>v.y>0).every(v=>v.paused.every(Boolean))).toBe(true);
    expect(record.report.restoration).toBe('verified by read-back');
  });
});

test('a later page pause overrides an earlier video play request during scrolling capture',async()=>{
  await runCapture(async page=>page.evaluate(async()=>{
    document.body.innerHTML='<div style="height:3500px;background:linear-gradient(white,#ccc)"><video id="stopped" muted loop width="180" height="100"></video><video id="last-play" muted loop width="180" height="100"></video></div>';
    const canvas=document.createElement('canvas');canvas.width=180;canvas.height=100;
    const context=canvas.getContext('2d'),stream=canvas.captureStream(30),recorder=new MediaRecorder(stream,{mimeType:'video/webm;codecs=vp8'}),parts=[];
    recorder.ondataavailable=event=>parts.push(event.data);const stopped=new Promise(resolve=>recorder.onstop=resolve);recorder.start();
    for(let index=0;index<30;index++){context.fillStyle=index%2?'#04c870':'#0088ff';context.fillRect(0,0,180,100);await new Promise(resolve=>setTimeout(resolve,35));}
    recorder.stop();await stopped;stream.getTracks().forEach(track=>track.stop());
    const source=URL.createObjectURL(new Blob(parts,{type:'video/webm'}));
    for(const video of document.querySelectorAll('video')){video.src=source;video.muted=true;await video.play();video.pause();video.currentTime=.2;}
    window.originalPlay=HTMLMediaElement.prototype.play;window.originalPause=HTMLMediaElement.prototype.pause;
    window.playbackRequests=[];let requested=false,paused=false;
    addEventListener('scroll',()=>{
      if(scrollY>500&&!requested){requested=true;document.querySelector('#stopped').play();document.querySelector('#last-play').play();window.playbackRequests.push('play');}
      if(scrollY>1500&&!paused){paused=true;document.querySelector('#stopped').pause();window.playbackRequests.push('pause');}
    });
  }),async({page,record})=>{
    await page.bringToFront();
    const after=await page.evaluate(()=>({
      stopped:{paused:document.querySelector('#stopped').paused,time:document.querySelector('#stopped').currentTime},
      playOnlyPaused:document.querySelector('#last-play').paused,
      playRestored:HTMLMediaElement.prototype.play===window.originalPlay,
      pauseRestored:HTMLMediaElement.prototype.pause===window.originalPause,
      requests:window.playbackRequests,
      captureStyles:document.querySelectorAll('style[data-winshot-capture]').length
    }));
    expect(after.requests).toEqual(['play','pause']);
    expect(after.stopped.paused,'the page explicitly paused this video after requesting play').toBe(true);
    expect(after.stopped.time).toBeCloseTo(.2,2);
    expect(after.playOnlyPaused,'capture-internal pauses must not erase a play-only request').toBe(false);
    expect(after.playRestored).toBe(true);expect(after.pauseRestored).toBe(true);
    expect(after.captureStyles).toBe(0);
    expect(record.report.restoration).toBe('verified by read-back');
  });
});

test('an installed version 2 media bridge is replaced before an upgraded capture',async()=>{
  await runCapture(async page=>page.evaluate(()=>{
    window.disposedMediaBridgeV2=false;window.obsoleteMediaRequests=0;
    const receive=event=>{
      if(event.source===window&&event.data?.source==='winshot-effects-request-v2'){
        window.obsoleteMediaRequests++;
        postMessage({source:'winshot-effects-response-v2',nonce:event.data.nonce,result:{error:'Obsolete media bridge'}},'*');
      }
    };
    addEventListener('message',receive);
    window[Symbol.for('winshot.page-effects')]={version:2,dispose(){window.disposedMediaBridgeV2=true;removeEventListener('message',receive);}};
  }),async({page,record})=>{
    expect(await page.evaluate(()=>({disposed:window.disposedMediaBridgeV2,obsoleteRequests:window.obsoleteMediaRequests,version:window[Symbol.for('winshot.page-effects')].version}))).toEqual({disposed:true,obsoleteRequests:0,version:3});
    expect(record.report.restoration).toBe('verified by read-back');
  });
});

test('clipboard and print contain the assembled full capture without editor controls',async()=>{
  await runCapture(null,async({browser,id,editor,record})=>{
    await editor.bringToFront();await editor.click('#copy-clipboard');
    await editor.waitForFunction(()=>/Copied|Copy failed/.test(document.querySelector('#toast').textContent));
    expect(await editor.$eval('#toast',n=>n.textContent)).toBe('Copied to clipboard.');
    // Grant only the test's subsequent clipboard READ. The real Copy click above
    // must work using its own user activation, without a pre-granted permission.
    await browser.defaultBrowserContext().overridePermissions(`chrome-extension://${id}`,['clipboard-read','clipboard-write']);
    const actual=await editor.evaluate(async()=>{
      const entries=await navigator.clipboard.read(),b=await entries.find(e=>e.types.includes('image/png')).getType('image/png');
      const image=await createImageBitmap(b),size={width:image.width,height:image.height};image.close();return size;
    });
    expect(actual).toEqual(record.report.dimensionsPx);
    await editor.click('#print-capture');
    await editor.waitForFunction(()=>document.querySelector('#print-image')?.complete&&document.querySelector('#print-capture').textContent==='Print');
    await editor.emulateMediaType('print');
    expect(await editor.evaluate(()=>({header:getComputedStyle(document.querySelector('header')).display,main:getComputedStyle(document.querySelector('main')).display,image:getComputedStyle(document.querySelector('#print-image')).display,width:document.querySelector('#print-image').naturalWidth,height:document.querySelector('#print-image').naturalHeight}))).toEqual({header:'none',main:'none',image:'block',...actual});
    const bytes=await editor.pdf({format:'A4',printBackground:true});
    const task=getDocument({data:new Uint8Array(bytes),verbosity:0,isEvalSupported:false});
    try{
      const doc=await task.promise;expect(doc.numPages).toBeGreaterThan(1);
      for(let n=1;n<=doc.numPages;n++)expect((await(await doc.getPage(n)).getTextContent()).items.map(x=>x.str).join(' ')).not.toContain('Save PNG');
    }finally{await task.destroy();}
  });
});
