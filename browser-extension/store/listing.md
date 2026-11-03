# Chrome Web Store listing

Copy these into the Developer Dashboard when creating the item. Screenshots (1280×800) and the
small promo tile (440×280) are in this folder.

## Store listing tab

**Name:** WinShot Capture

**Summary (132 characters max):**
Capture full pages, selections and elements. Mark them up, then save as PNG, JPEG, WebP or searchable PDF. Nothing leaves your PC.

**Category:** Tools

**Language:** English

**Description:**

WinShot Capture takes screenshots of web pages the way you need them, and lets you mark them up before you save or share.

CAPTURE
• Entire page, even very long and endlessly scrolling pages, without a giant blurry image
• Visible part of the page
• A selection you drag (it scrolls the page for you at the edges; hold Ctrl for a square, Alt for full width)
• A single page element, a scrolling panel, or a frame
• Every open tab, or a list of web addresses, in one go — or all tabs into one PDF
• Delayed capture, keyboard shortcuts, and a right-click menu
• Sticky headers and floating bars appear once, not on every screen

MARK UP
• Pen and highlighter
• Rectangle, ellipse, line, arrow, and numbered steps
• Text in plain, bold, outlined, or label styles
• Pixelate, blur, or black out private details
• Spotlight to dim everything except what matters
• Crop and resize; unlimited undo and redo
• Your marks are saved with the capture, so you can come back and change them

SAVE AND SHARE
• PNG, JPEG, WebP, or PDF with searchable, copyable text and working links
• PDF page sizes (A4, Letter, Legal, custom), headers, footers, and watermarks
• Copy to the clipboard or print
• Choose what happens after each capture: open the editor, save straight away, save a PDF, or copy
• Pick a Downloads sub-folder and a file-name pattern (FireShot-style %t %y-%m-%d tokens work too)
• Capture history with search and thumbnails; open or paste any image to edit it

PRIVATE BY DESIGN
Everything happens in your browser. WinShot Capture has no account, no server, no analytics, and never uploads your pages or screenshots.

WORKS WITH WINSHOT FOR WINDOWS
With the WinShot desktop app installed, send a capture to its editor in one click, or capture the whole browser window.

## Privacy practices tab

**Single purpose:**
Capture screenshots of web pages and let the user annotate and save them.

**Permission justifications:**

| Permission | Why |
| --- | --- |
| activeTab | Capture the tab the user clicked the toolbar button or shortcut on. |
| tabs | Read the page title and address for file names and history, keep captures on the right tab, and capture all open tabs when asked. |
| scripting | Scroll the page and measure it while capturing a full page, selection, or element, then restore it exactly. |
| Host permission (all sites) | Capture full pages and frames on any site the user chooses, including all-tabs and URL-list batches, without asking site by site. Nothing is sent anywhere. |
| downloads | Save captures as PNG, JPEG, WebP, or PDF into the folder the user picks. |
| storage | Keep settings and the local capture history. |
| contextMenus | Offer capture actions on the right-click menu. |
| alarms | Prune capture history older than the user's retention setting once a day. |
| webNavigation | Find the exact frame the user picked when capturing inside an iframe. |
| nativeMessaging (optional) | Reserved for talking to the WinShot desktop app; only requested if the user turns that on. |

**Remote code:** No, I am not using remote code.

**Data usage:** Do not tick any data type. Certify that data is not sold, not used for unrelated purposes, and not used for credit decisions.

**Privacy policy URL:** Not required when no user data is collected. If the dashboard insists, use a page with this text:

> WinShot Capture does not collect, transmit, or sell any personal or browsing data. Screenshots, settings, and history are stored only in your browser's local storage on your device and can be deleted from the History page at any time.
