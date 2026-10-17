# Batch actions and the opt-in website API

In the popup, **Remember action for** stores a separate after-capture action for
Entire page, Visible viewport, Selection and Element/frame. A complete capture
opens its editor and optionally downloads the chosen format once. Reloading the
editor does not repeat that download. Partial/failed captures open for review.

Batch capture supports this window, its selected tabs (Ctrl/Cmd-click the tab
strip), or all windows. Choose visible/full-page capture and a result action.
Individual image/PDF files are delivered in one ZIP, avoiding a separate save
prompt for every tab. Only complete captures enter that ZIP. Partial captures
remain available individually for review. One combined PDF is also available.
The existing 20-tab/URL safety limit and sequential capture queue still apply.
Stopping a batch retains its completed results. ZIP archives are limited to
512 MB; large captures can still be saved individually from History.

## Enable a website deliberately

Website automation is off by default. Open the extension popup, expand
**Website automation**, add exact origins such as `https://example.com`, select
**Allow listed websites**, and choose **Apply website access**. Paths, credentials,
queries, wildcard hosts and non-HTTP(S) addresses are rejected. Up to 20 exact
origins may be granted. Subdomains and different ports need separate grants.

An allowed top-level page gets `window.WinShotCapture`; existing pages are
updated when access is applied. New page loads receive it automatically. No API
is injected into child frames. Calls are independently rechecked in the extension
against Chrome's actual sender tab, frame and origin. Capture requests use the
page's own tab and the ordinary window lock, screenshot quota, cancellation and
restoration paths. Another capture/batch may make a request fail as busy.

```js
// Wait for the winshot-ready event if the extension is still attaching.
const request = WinShotCapture.capture({ mode: "full-page" });
console.log(request.requestId);
// Optional: await request.cancel();  // cancels only this page's request
const { captureId, state } = await request;
```

Only `visible` and `full-page` are supported. Completion returns a local capture
identifier and state, never image bytes, page text or other tabs' capture data.
The result opens in the WinShot editor; website requests do not trigger remembered
automatic downloads. No supplied website JavaScript is executed by WinShot.

To revoke, turn automation off or remove the website, then Apply. Outstanding
requests for revoked origins are cancelled and the injected API is disposed.
The page can never cancel a manual capture, another origin's request, or a batch.
Reloading/removing the extension requires refreshing the website before reuse.

## Open-source review

The original [GoFullPage CaptureAPI](https://github.com/mrcoles/full-page-screen-capture-chrome-extension/blob/master/api.js)
(MIT) separates privileged tab capture from the page-side capture coordinator and
opens completed results in extension tabs. That is an internal extension API,
not an origin permission system. WinShot retains that separation and adds its
own explicit origin grants, sender/frame checks and request cancellation. No
upstream code was copied and no runtime package was introduced. Chrome's official
extension samples are Apache-2.0 licensed; this implementation uses the existing
MV3 messaging/scripting APIs rather than executing arbitrary user scripts.
