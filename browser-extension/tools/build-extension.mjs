import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const here = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(here, "..");
const output = path.join(project, "dist", "winshot-capture");

fs.rmSync(output, { recursive: true, force: true });
fs.mkdirSync(output, { recursive: true });
for (const name of ["manifest.json", "README.md", "THIRD_PARTY_NOTICES.md"]) {
  fs.copyFileSync(path.join(project, name), path.join(output, name));
}
fs.cpSync(path.join(project, "src"), path.join(output, "src"), { recursive: true });
fs.cpSync(path.join(project, "docs"), path.join(output, "docs"), { recursive: true });
fs.cpSync(path.join(project, "icons"), path.join(output, "icons"), { recursive: true });

const manifest = JSON.parse(fs.readFileSync(path.join(output, "manifest.json"), "utf8"));
if (manifest.manifest_version !== 3 || !manifest.background?.service_worker) {
  throw new Error("Packaged extension is not a valid MV3 service-worker package.");
}
// Written last, after every other file is in place: an unpacked (developer) install
// polls this and reloads itself when it changes (src/dev-reload.js).
let commit = "unknown";
try { commit = execFileSync("git", ["rev-parse", "--short", "HEAD"], { cwd: project, encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim(); } catch { /* not a git checkout */ }
const builtAt = new Date().toISOString();
fs.writeFileSync(path.join(output, "src", "build-stamp.json"), JSON.stringify({ id: `${manifest.version}-${commit}-${builtAt}`, version: manifest.version, commit, builtAt }, null, 2));
console.log(`Built ${manifest.name} ${manifest.version} (${commit}) at ${output}`);
