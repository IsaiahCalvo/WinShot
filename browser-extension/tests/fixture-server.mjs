import http from "node:http";

const host = "127.0.0.1";

function shell(title, body, script = "", style = "") {
  return `<!doctype html><html><head><meta charset="utf-8"><title>${title}</title><style>
    *{box-sizing:border-box}html,body{margin:0;font:16px/1.4 Arial,sans-serif}body{background:#fff;color:#172033}
    .row{height:120px;padding:16px;border-bottom:1px solid #73819a;background:linear-gradient(90deg,#fff,#e8eef8)}
    ${style}
  </style></head><body data-fixture="${title}">${body}<script>${script}</script></body></html>`;
}

function rows(count, prefix = "ROW") {
  return Array.from({ length: count }, (_, i) => `<div class="row" data-row="${i}">${prefix} ${String(i).padStart(4, "0")}</div>`).join("");
}

function fixture(name, crossPort) {
  switch (name) {
    case "body":
      return shell("body-scroll", `<h1>SEARCHABLE WINSHOT DOCUMENT</h1><p>検索可能 مرحبا</p><a href="https://example.com/winshot-link">WORKING PDF LINK</a><input id="state" value="focus-restoration"><div id="rows">${rows(28)}</div>`, "");
    case "horizontal":
      return shell("horizontal-scroll", `<div class="wide">${Array.from({length:18},(_,i)=>`<span>COL ${i}</span>`).join("")}</div>`, "", ".wide{display:flex;width:5400px;height:900px}.wide span{flex:0 0 300px;border-right:2px solid #172033;padding:30px}");
    case "nested":
      return shell("nested-scrollers", `<div id="outer"><div class="spacer">OUTER TOP</div><div id="inner"><div class="wide">${Array.from({length:30},(_,i)=>`<div class="card">NESTED ${i}</div>`).join("")}</div></div><div class="spacer">OUTER BOTTOM</div></div>`, "", "#outer{margin:30px;width:900px;height:600px;overflow:auto;border:5px solid #234}.spacer{height:500px;padding:30px}#inner{width:700px;height:360px;overflow:auto;border:5px solid #1687ff}.wide{display:grid;grid-template-columns:repeat(3,320px);width:960px}.card{height:180px;border:1px solid #333;padding:20px}");
    case "sticky":
      return shell("sticky-fixed", `<header>STICKY HEADER — ONCE</header><div>${rows(34)}</div><footer>FIXED FOOTER — ONCE</footer>`, "", "header{position:sticky;top:0;height:54px;padding:15px;background:#ffcf33;z-index:2}footer{position:fixed;bottom:0;width:100%;height:46px;padding:12px;background:#33c89b;z-index:2}");
    case "lazy":
      return shell("lazy-content", `<div id="lazy">${Array.from({length:35},(_,i)=>`<div class="lazy" data-index="${i}">WAIT ${i}</div>`).join("")}</div>`, `
        const observer=new IntersectionObserver(entries=>entries.forEach(entry=>{if(entry.isIntersecting){entry.target.textContent='LOADED '+entry.target.dataset.index;entry.target.dataset.loaded='true';observer.unobserve(entry.target)}}),{rootMargin:'300px'});
        document.querySelectorAll('.lazy').forEach(node=>observer.observe(node));
      `, ".lazy{height:180px;padding:30px;border-bottom:1px solid #555;background:linear-gradient(120deg,#fff,#d6f5e9)}");
    case "animated":
      return shell("animated-changing", `<div class="spinner">ANIMATED</div>${rows(18)}`, "", ".spinner{height:100px;padding:30px;background:#f88;animation:pulse .2s infinite alternate}@keyframes pulse{to{background:#88f;transform:translateX(2px)}}");
    case "repetitive":
      return shell("repetitive-table", `<table>${Array.from({length:200},(_,i)=>`<tr><td>${i}</td><td>Repeated value</td><td>Repeated value</td></tr>`).join("")}</table><div style="height:1200px"></div>`, "", "table{width:100%;border-collapse:collapse}td{height:32px;border:1px solid #aaa}");
    case "shadow":
      return shell("shadow-dom", `<fixture-list></fixture-list>`, `customElements.define('fixture-list',class extends HTMLElement{connectedCallback(){const root=this.attachShadow({mode:'open'});root.innerHTML='<style>.item{height:130px;border-bottom:1px solid #1687ff;padding:20px}</style>'+Array.from({length:30},(_,i)=>'<div class="item">SHADOW '+i+'</div>').join('')}})`);
    case "iframes":
      return shell("frames", `<h1>Frames</h1><iframe src="/frame"></iframe><iframe src="http://${host}:${crossPort}/frame"></iframe><iframe src="/nested-frame-host"></iframe><iframe src="/nested-cross-frame-host"></iframe>${rows(12)}`, "", "iframe{width:90%;height:350px;margin:20px;border:4px solid #1687ff}");
    case "duplicate-frames":
      return shell("duplicate-frames", `<h1>Duplicate frames</h1><iframe data-copy="first" src="http://${host}:${crossPort}/frame"></iframe><iframe data-copy="second" src="http://${host}:${crossPort}/frame"></iframe>`, "", "iframe{display:block;width:900px;height:350px;margin:20px;border:4px solid #1687ff}");
    case "nested-frame-host":
      return shell("nested-frame-host", `<iframe src="/frame"></iframe>`, "", "iframe{width:900px;height:300px;margin:20px;border:4px solid #f4bd00}");
    case "nested-cross-frame-host":
      return shell("nested-cross-frame-host", `<iframe src="http://${host}:${crossPort}/frame"></iframe>`, "", "iframe{width:900px;height:300px;margin:20px;border:4px solid #f4bd00}");
    case "frame":
      return shell("frame-content", rows(14, "FRAME"));
    case "long":
      return shell("beyond-32k", rows(290, "LONG"));
    case "infinite":
      return shell("bounded-infinite", `<div id="feed">${rows(12,"FEED")}</div>`, `let page=1;addEventListener('scroll',()=>{if(scrollY+innerHeight>document.body.scrollHeight-400&&page<12){document.querySelector('#feed').insertAdjacentHTML('beforeend',${JSON.stringify(rows(8,"APPEND")).replaceAll("APPEND", "APPEND '+page+'")});page++}});`);
    case "virtual":
      return shell("virtualized-feed", `<div id="virtual"><div id="rail"></div></div>`, `
        const total=240,itemHeight=64,view=document.querySelector('#virtual'),rail=document.querySelector('#rail');rail.style.height=(total*itemHeight)+'px';
        function render(){const first=Math.floor(view.scrollTop/itemHeight);rail.replaceChildren(...Array.from({length:14},(_,slot)=>{const index=Math.min(total-1,first+slot);const node=document.createElement('div');node.className='virtual-row';node.style.transform='translateY('+(index*itemHeight)+'px)';node.textContent='VIRTUAL '+String(index).padStart(4,'0');node.dataset.index=index;return node}))}view.addEventListener('scroll',render);render();
      `, "#virtual{margin:30px;width:760px;height:520px;overflow:auto;border:4px solid #1687ff;position:relative}#rail{position:relative}.virtual-row{position:absolute;left:0;right:0;height:64px;padding:18px;border-bottom:1px solid #556;background:#fff}");
    case "media":
      return shell("pixel-surfaces", `<canvas id="two" width="700" height="500"></canvas><canvas id="gl" width="700" height="500"></canvas><video controls width="700"></video>${rows(10)}`, `const c=document.querySelector('#two').getContext('2d');c.fillStyle='#1687ff';c.fillRect(0,0,700,500);c.fillStyle='white';c.font='40px sans-serif';c.fillText('CANVAS PIXELS',100,250);const gl=document.querySelector('#gl').getContext('webgl');gl.clearColor(.1,.7,.3,1);gl.clear(gl.COLOR_BUFFER_BIT);`);
    case "recovery":
      return shell("recovery", `<input id="focus" value="original"><div id="snap" style="scroll-snap-type:y mandatory;height:500px;overflow:auto">${rows(20,"RECOVERY")}</div>`);
    default:
      return shell("fixture-index", `<h1>WinShot fixture corpus</h1><ul>${["body","horizontal","nested","sticky","lazy","animated","repetitive","shadow","iframes","duplicate-frames","long","infinite","virtual","media","recovery"].map(value=>`<li><a href="/${value}">${value}</a></li>`).join("")}</ul>`);
  }
}

function start(port, crossPort) {
  return http.createServer((request, response) => {
    const name = new URL(request.url, `http://${host}:${port}`).pathname.slice(1) || "index";
    const body = fixture(name, crossPort);
    response.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
    response.end(body);
  }).listen(port, host, () => console.log(`fixture server ${port}`));
}

const primary = start(4173, 4174);
const cross = start(4174, 4173);
for (const signal of ["SIGINT", "SIGTERM"]) process.on(signal, () => {
  primary.close(); cross.close(); process.exit(0);
});
