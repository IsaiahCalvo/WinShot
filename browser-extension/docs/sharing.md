# Share an edited screenshot

Open **Share** in the image editor. Every sharing action renders the current
crop, annotations and image adjustments into a normal PNG. Editable projects
and their original source pixels are not attached to these outputs.

- **Copy for OneNote** writes an image, rich HTML and source-title/link text to
  the clipboard. Open a note in OneNote and paste. This works through the
  clipboard; it does not sign into Microsoft or upload to a notebook directly.
- **Save email draft** downloads an `.eml` message with the PNG attached. Open
  it in a desktop mail application, choose the sending account and recipient,
  then send. The `X-Unsent` header requests draft mode; some mail applications
  instead open `.eml` as a received message. Webmail does not universally import
  draft files: save the PNG and attach it there if needed. The extension never
  sends mail itself. Attachments are limited to 18 MB before MIME encoding;
  actual mail providers may impose smaller limits.
- **Save image note** downloads an offline HTML file containing the edited
  image, title and source link. It does not depend on remote images or scripts.
- **Prepare file to share or drag** renders a file first. If Chrome and the OS
  support file sharing, a second click opens their share sheet. Otherwise drag
  the image to an application that accepts Chromium file drags, or click the
  link to save it. The prepared copy represents the edits at preparation time.
  Closing and reopening Share prepares a fresh copy.

Clipboard, HTML-note and prepared-file sharing are bounded to 32 MB per PNG.
Use an ordinary image/PDF export for a larger capture. OS share-sheet contents,
desktop drag targets, mail draft support and OneNote paste behavior depend on
the installed applications. Browser tests independently parse MIME attachments,
decode the exported image, and read actual clipboard formats; they do not
automate the user's installed mail/OneNote applications.

Email composition reuses the MIT-licensed MIMEText browser bundle. Clipboard
handling uses the native multi-format Clipboard API, following the approach
documented by Google's web.dev and the MIT clipboard-polyfill project.
See [third-party notices](../THIRD_PARTY_NOTICES.md) for source and licenses.
