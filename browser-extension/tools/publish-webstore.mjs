// Uploads (and optionally publishes) the packaged extension through the Chrome Web
// Store API v2. No dependencies: Node 20+ fetch and crypto only.
//
//   node browser-extension/tools/publish-webstore.mjs <zip> [--upload-only] [--staged] [--percentage N] [--status]
//
// Environment (one of the two credential sets):
//   CWS_EXTENSION_ID, CWS_PUBLISHER_ID                      always
//   CWS_SERVICE_ACCOUNT_JSON                                 service-account key (recommended: never expires)
//   CWS_CLIENT_ID, CWS_CLIENT_SECRET, CWS_REFRESH_TOKEN      or an OAuth client + refresh token
//
// See browser-extension/docs/chrome-web-store.md for the one-time setup.
import crypto from "node:crypto";
import fs from "node:fs";

const SCOPE = "https://www.googleapis.com/auth/chromewebstore";
const API = "https://chromewebstore.googleapis.com";

function fail(message) {
  console.error(`::error::${message}`);
  process.exit(1);
}

function base64url(value) {
  return Buffer.from(value).toString("base64").replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

async function tokenFromServiceAccount(json) {
  let key;
  try { key = JSON.parse(json); } catch { fail("CWS_SERVICE_ACCOUNT_JSON is not valid JSON."); }
  const now = Math.floor(Date.now() / 1000);
  const header = base64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const claims = base64url(JSON.stringify({
    iss: key.client_email, scope: SCOPE, aud: key.token_uri || "https://oauth2.googleapis.com/token", iat: now, exp: now + 3600
  }));
  const signature = crypto.createSign("RSA-SHA256").update(`${header}.${claims}`).sign(key.private_key);
  const response = await fetch(key.token_uri || "https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion: `${header}.${claims}.${base64url(signature)}` })
  });
  const body = await response.json();
  if (!response.ok) fail(`Service-account token request failed: ${body.error_description || body.error || response.status}`);
  return body.access_token;
}

async function tokenFromRefreshToken() {
  const response = await fetch("https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      client_id: process.env.CWS_CLIENT_ID,
      client_secret: process.env.CWS_CLIENT_SECRET,
      refresh_token: process.env.CWS_REFRESH_TOKEN,
      grant_type: "refresh_token"
    })
  });
  const body = await response.json();
  if (!response.ok) {
    const hint = body.error === "invalid_grant"
      ? " The refresh token expired or was revoked. If the OAuth consent screen is still in Testing, Google expires refresh tokens after 7 days: publish the consent screen to Production, or switch to a service account."
      : "";
    fail(`OAuth token refresh failed: ${body.error_description || body.error || response.status}.${hint}`);
  }
  return body.access_token;
}

async function accessToken() {
  if (process.env.CWS_SERVICE_ACCOUNT_JSON) return await tokenFromServiceAccount(process.env.CWS_SERVICE_ACCOUNT_JSON);
  if (process.env.CWS_CLIENT_ID && process.env.CWS_CLIENT_SECRET && process.env.CWS_REFRESH_TOKEN) return await tokenFromRefreshToken();
  fail("No Chrome Web Store credentials. Set CWS_SERVICE_ACCOUNT_JSON, or CWS_CLIENT_ID + CWS_CLIENT_SECRET + CWS_REFRESH_TOKEN.");
}

async function call(token, method, url, body = undefined, contentType = undefined) {
  const response = await fetch(url, {
    method,
    headers: { authorization: `Bearer ${token}`, ...(contentType ? { "content-type": contentType } : {}) },
    body
  });
  const text = await response.text();
  let json;
  try { json = text ? JSON.parse(text) : {}; } catch { json = { raw: text }; }
  if (!response.ok) fail(`${method} ${url.replace(API, "")} failed (${response.status}): ${json.error?.message || text}`);
  return json;
}

const args = process.argv.slice(2);
const zipPath = args.find((value) => !value.startsWith("--") && !/^\d+$/.test(value));
const uploadOnly = args.includes("--upload-only");
const statusOnly = args.includes("--status");
const staged = args.includes("--staged");
const percentageIndex = args.indexOf("--percentage");
const percentage = percentageIndex >= 0 ? Number(args[percentageIndex + 1]) : null;

const extensionId = process.env.CWS_EXTENSION_ID;
const publisherId = process.env.CWS_PUBLISHER_ID;
if (!extensionId) fail("CWS_EXTENSION_ID is not set (the 32-letter ID from the Developer Dashboard).");
if (!publisherId) fail("CWS_PUBLISHER_ID is not set (Developer Dashboard > Account > Publisher ID).");
const item = `${API}/v2/publishers/${encodeURIComponent(publisherId)}/items/${encodeURIComponent(extensionId)}`;
const uploadUrl = `${API}/upload/v2/publishers/${encodeURIComponent(publisherId)}/items/${encodeURIComponent(extensionId)}:upload`;

const token = await accessToken();

if (statusOnly) {
  console.log(JSON.stringify(await call(token, "GET", `${item}:fetchStatus`), null, 2));
  process.exit(0);
}

if (!zipPath || !fs.existsSync(zipPath)) fail(`Package not found: ${zipPath || "(none given)"}`);
console.log(`Uploading ${zipPath}…`);
let upload = await call(token, "POST", uploadUrl, fs.readFileSync(zipPath), "application/zip");
const deadline = Date.now() + 5 * 60_000;
const pending = (state) => /IN_PROGRESS/.test(state || "");
while (pending(upload.uploadState) && Date.now() < deadline) {
  await new Promise((resolve) => setTimeout(resolve, 5000));
  const status = await call(token, "GET", `${item}:fetchStatus`);
  upload = { ...upload, uploadState: status.lastAsyncUploadState || status.uploadState || upload.uploadState, status };
}
if (pending(upload.uploadState) || /FAIL|NOT_FOUND/.test(upload.uploadState || "")) {
  fail(`Upload did not succeed: ${JSON.stringify(upload)}`);
}
console.log(`Uploaded version ${upload.crxVersion || "(see dashboard)"}.`);

if (uploadOnly) {
  console.log("Upload only: the new version is saved as a draft in the Developer Dashboard and was not submitted for review.");
  process.exit(0);
}

const publishBody = { publishType: staged ? "STAGED_PUBLISH" : "DEFAULT_PUBLISH" };
if (Number.isFinite(percentage)) publishBody.deployInfos = [{ deployPercentage: Math.max(0, Math.min(100, percentage)) }];
const published = await call(token, "POST", `${item}:publish`, JSON.stringify(publishBody), "application/json");
console.log(`Submitted for review: ${JSON.stringify(published)}`);
console.log(staged
  ? "Staged: once review passes, the version waits in the dashboard until you press Publish."
  : "Google reviews every version; it goes live automatically once review passes (usually hours, sometimes days).");
