# Publishing to the Chrome Web Store

Releases are headless: raise `version` in `browser-extension/manifest.json`, merge, then run
the **publish-browser-extension** workflow (Actions tab, or the GitHub API). It runs the unit
tests, builds `winshot-capture-<version>.zip`, uploads it through the Chrome Web Store API v2,
and either saves it as a draft (`upload-only`), submits it for review (`publish`), or submits it
and waits for a manual Publish click (`staged`). Google reviews every version; it goes live by
itself once review passes.

Every pull request that touches the extension also runs **browser-extension** CI, which runs the
unit and end-to-end suites and attaches the store zip as a build artifact.

## One-time setup (owner only)

These steps need the owner's Google account and payment details, so they cannot be automated.

1. **Developer account.** Sign in at the
   [Chrome Web Store Developer Dashboard](https://chrome.google.com/webstore/devconsole) and pay the
   one-time US$5 registration fee if the account is new.
2. **Create the item once by hand.** The API can update an item but cannot create one.
   In the dashboard choose **New item** and upload `winshot-capture-<version>.zip` (download it from
   the latest browser-extension CI run's artifacts, or run `npm run package:extension`).
   Fill in the listing from [`../store/listing.md`](../store/listing.md) and the screenshots in
   `../store/`. Note the 32-letter **item ID** shown for the new item.
3. **Publisher ID.** Dashboard > **Account**: copy the **Publisher ID**.
4. **Service account** (recommended: it never expires).
   1. In [Google Cloud Console](https://console.cloud.google.com/) create a project (or pick one).
   2. **APIs & Services > Library**: enable **Chrome Web Store API**.
   3. **IAM & Admin > Service accounts > Create service account**. No roles are needed.
   4. Open the new account > **Keys > Add key > Create new key > JSON**. A `.json` file downloads.
   5. Back in the Developer Dashboard > **Account > Service accounts**, add the service account's
      email address (it ends in `iam.gserviceaccount.com`). One service account per publisher.
5. **GitHub secrets.** Repository **Settings > Secrets and variables > Actions > New repository
   secret**:

   | Secret | Value |
   | --- | --- |
   | `CWS_EXTENSION_ID` | The item ID from step 2 |
   | `CWS_PUBLISHER_ID` | The publisher ID from step 3 |
   | `CWS_SERVICE_ACCOUNT_JSON` | The entire contents of the JSON key file from step 4 |

   Optional: under **Settings > Environments** add an environment named `chrome-web-store` with
   yourself as a required reviewer, so every store submission waits for a click.

### OAuth alternative

If a service account is not possible, create an OAuth client instead (Cloud Console > Google Auth
Platform > Clients > **Web application**, redirect URI
`https://developers.google.com/oauthplayground`), get a refresh token for scope
`https://www.googleapis.com/auth/chromewebstore` in the
[OAuth Playground](https://developers.google.com/oauthplayground) with "Use your own OAuth
credentials", and store `CWS_CLIENT_ID`, `CWS_CLIENT_SECRET`, and `CWS_REFRESH_TOKEN` instead of
`CWS_SERVICE_ACCOUNT_JSON`. Publish the OAuth consent screen to **Production**: while it is in
Testing, Google expires refresh tokens after 7 days and releases start failing with
`invalid_grant`.

## Running a release

1. Raise `version` in `browser-extension/manifest.json` (the store rejects a version it already has).
2. Merge to `main`.
3. Actions > **publish-browser-extension** > **Run workflow** > choose the action.

From a terminal with the same environment variables set, the equivalent is:

```bash
npm run package:extension
node browser-extension/tools/publish-webstore.mjs browser-extension/dist/winshot-capture-<version>.zip --upload-only
node browser-extension/tools/publish-webstore.mjs --status
```
