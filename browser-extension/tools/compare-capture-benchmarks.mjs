import fs from "node:fs";
import path from "node:path";
import { parseArgs } from "node:util";

const { values } = parseArgs({ options: {
  baseline: { type: "string" }, candidate: { type: "string" }, output: { type: "string", default: "output/capture-benchmark/comparison.json" }
} });
if (!values.baseline || !values.candidate) throw new Error("Provide --baseline and --candidate filename prefixes");
function read(prefix) {return {summary:JSON.parse(fs.readFileSync(`${prefix}-summary.json`)),runs:JSON.parse(fs.readFileSync(`${prefix}-runs.json`))};}
const baseline=read(values.baseline),candidate=read(values.candidate);
const percentile=(numbers,p)=>{const sorted=[...numbers].sort((a,b)=>a-b);return sorted[Math.max(0,Math.ceil(sorted.length*p)-1)]??null;};
const comparisons=['browser','platform','scenario','fixtureSha256','viewport','graphics','measurement','networkCachePolicy','profileFirstAttempt'].map(key=>({key,equal:JSON.stringify(baseline.summary[key])===JSON.stringify(candidate.summary[key])}));
if(baseline.summary.scenario==='public'){
  const shapes=cohort=>[...new Set(cohort.runs.map(run=>JSON.stringify({dimensions:run.report?.dimensionsPx,tiles:run.report?.tileCount})))].sort();
  comparisons.push({key:'public captured dimensions and tile count',equal:JSON.stringify(shapes(baseline))===JSON.stringify(shapes(candidate))},{key:'public URL',equal:baseline.summary.url===candidate.summary.url});
}
const hashes=cohort=>[...new Set(cohort.runs.map(run=>run.audit?.pngSha256).filter(Boolean))];
const baselineHashes=hashes(baseline),candidateHashes=hashes(candidate);
const pixelHashesMatch=baseline.summary.scenario==='public'?null:baselineHashes.length===1&&candidateHashes.length===1&&baselineHashes[0]===candidateHashes[0];
function stats(cohort){
  const gaps=cohort.runs.flatMap(run=>{const calls=(run.trace||[]).filter(entry=>entry.api==='captureVisibleTab');return calls.slice(1).map((entry,index)=>entry.start-calls[index].start);});
  const positionAndStylesRestoredCount=cohort.runs.filter(run=>run.restored&&run.restored.x===run.source.x&&run.restored.y===run.source.y&&!run.restored.captureStyles&&!run.restored.pickers).length;
  const fullyRestoredCount=cohort.runs.filter(run=>run.report?.restoration==='verified by read-back'&&run.restored&&run.restored.x===run.source.x&&run.restored.y===run.source.y&&!run.restored.captureStyles&&!run.restored.pickers).length;
  const strictRuns=cohort.runs.filter(run=>run.correct&&run.report?.coverage?.complete===true&&run.report?.restoration==='verified by read-back'&&run.restored&&run.restored.x===run.source.x&&run.restored.y===run.source.y&&!run.restored.captureStyles&&!run.restored.pickers);
  return {samples:cohort.runs.length,passed:strictRuns.length,failed:cohort.runs.length-strictRuns.length,originalReportedPassed:cohort.summary.passed,p50Ms:percentile(strictRuns.map(run=>run.elapsedMs),.5),p95Ms:percentile(strictRuns.map(run=>run.elapsedMs),.95),scrollP50Ms:percentile(strictRuns.map(run=>run.stages.scrollMs),.5),readbackP50Ms:percentile(strictRuns.map(run=>run.stages.readbackMs),.5),minReadbackCallGapMs:gaps.length?Math.min(...gaps):null,quotaErrors:cohort.runs.flatMap(run=>run.trace||[]).filter(entry=>entry.api==='captureVisibleTab'&&/MAX_CAPTURE_VISIBLE_TAB_CALLS_PER_SECOND|quota/i.test(entry.error||'')).length,positionAndStylesRestoredCount,fullyRestoredCount,allRestored:fullyRestoredCount===cohort.runs.length};
}
const comparison={baseline:stats(baseline),candidate:stats(candidate),comparability:comparisons,pixelHashesMatch,baselineHashes,candidateHashes,p50ImprovementPercent:baseline.summary.p50Ms&&candidate.summary.p50Ms?(1-candidate.summary.p50Ms/baseline.summary.p50Ms)*100:null,p95ImprovementPercent:baseline.summary.p95Ms&&candidate.summary.p95Ms?(1-candidate.summary.p95Ms/baseline.summary.p95Ms)*100:null,note:'Observed nearest-rank percentiles; small samples and shared CI hardware do not establish a population-wide speed guarantee. A negative improvement is a measured slowdown; no timing result is suppressed.'};
const passed=comparisons.every(item=>item.equal)&&comparison.baseline.failed===0&&comparison.candidate.failed===0&&comparison.baseline.allRestored&&comparison.candidate.allRestored&&comparison.baseline.quotaErrors===0&&comparison.candidate.quotaErrors===0&&pixelHashesMatch!==false;
comparison.p50ImprovementPercent=passed&&comparison.baseline.p50Ms&&comparison.candidate.p50Ms?(1-comparison.candidate.p50Ms/comparison.baseline.p50Ms)*100:null;
comparison.p95ImprovementPercent=passed&&comparison.baseline.p95Ms&&comparison.candidate.p95Ms?(1-comparison.candidate.p95Ms/comparison.baseline.p95Ms)*100:null;
comparison.passed=passed;
fs.mkdirSync(path.dirname(path.resolve(values.output)),{recursive:true});fs.writeFileSync(values.output,JSON.stringify(comparison,null,2));
const seconds=value=>value===null?'—':`${(value/1000).toFixed(3)} s`;
const markdown=`## Capture performance: ${candidate.summary.platform}\n\n| Cohort | Complete / attempts | Median | Observed p95 | Median scroll/settle | Restoration | Quota errors |\n|---|---:|---:|---:|---:|---|---:|\n${[['Baseline',comparison.baseline],['Candidate',comparison.candidate]].map(([name,row])=>`| ${name} | ${row.passed}/${row.samples} | ${seconds(row.p50Ms)} | ${seconds(row.p95Ms)} | ${seconds(row.scrollP50Ms)} | ${row.allRestored?'verified':'FAILED'} | ${row.quotaErrors} |`).join('\n')}\n\nExact composite PNG hashes match: **${pixelHashesMatch===null?'not an exact-content public-page oracle':pixelHashesMatch?'yes':'NO'}**. Browser/fixture/viewport/graphics comparisons: **${comparisons.every(item=>item.equal)?'equal':'MISMATCH'}**.\n\n${comparison.note}\n`;
fs.writeFileSync(values.output.replace(/\.json$/i,'')+'.md',markdown);
if(process.env.GITHUB_STEP_SUMMARY)fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,markdown);
console.log(JSON.stringify(comparison));if(!passed)process.exitCode=1;
