import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

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
console.log(`Built ${manifest.name} ${manifest.version} at ${output}`);
