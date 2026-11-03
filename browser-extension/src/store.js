const DB_NAME = "winshot-captures";
const DB_VERSION = 1;
const RECORDS = "records";
const TILES = "tiles";

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, DB_VERSION);
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(RECORDS)) db.createObjectStore(RECORDS, { keyPath: "id" });
      if (!db.objectStoreNames.contains(TILES)) {
        const store = db.createObjectStore(TILES, { keyPath: ["captureId", "index"] });
        store.createIndex("byCapture", "captureId");
      }
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

function requestResult(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}

export async function putRecord(record) {
  const db = await openDatabase();
  try {
    const tx = db.transaction(RECORDS, "readwrite");
    tx.objectStore(RECORDS).put(record);
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

// Imports become visible in History only when every raster is stored. A failed
// quota write aborts the whole transaction, leaving no empty imported record.
export async function putImportedCapture(record, tiles) {
  const db = await openDatabase();
  try {
    await new Promise((resolve, reject) => {
      const tx = db.transaction([RECORDS, TILES], "readwrite");
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error || new Error("Import was cancelled."));
      try {
        tx.objectStore(RECORDS).add(record);
        for (const tile of tiles) tx.objectStore(TILES).add({ ...tile, captureId: record.id });
      } catch (error) {
        tx.abort();
        reject(error);
      }
    });
  } finally { db.close(); }
}

// Put an updated record only if it still exists — a record the user deleted from
// History mid-capture must stay deleted. Returns false when the record is gone.
export async function updateRecord(record) {
  const db = await openDatabase();
  try {
    const tx = db.transaction(RECORDS, "readwrite");
    const store = tx.objectStore(RECORDS);
    const existing = await requestResult(store.get(record.id));
    if (existing !== undefined) store.put(record);
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
    return existing !== undefined;
  } finally {
    db.close();
  }
}

export async function getRecord(id) {
  const db = await openDatabase();
  try {
    return await requestResult(db.transaction(RECORDS).objectStore(RECORDS).get(id));
  } finally {
    db.close();
  }
}

export async function listRecords() {
  const db = await openDatabase();
  try {
    return await requestResult(db.transaction(RECORDS).objectStore(RECORDS).getAll());
  } finally {
    db.close();
  }
}

export async function putTile(tile) {
  const db = await openDatabase();
  try {
    const tx = db.transaction(TILES, "readwrite");
    tx.objectStore(TILES).put(tile);
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

export async function getTiles(captureId) {
  const db = await openDatabase();
  try {
    const index = db.transaction(TILES).objectStore(TILES).index("byCapture");
    return await requestResult(index.getAll(IDBKeyRange.only(captureId)));
  } finally {
    db.close();
  }
}

export async function getTile(captureId, index) {
  const db = await openDatabase();
  try {
    return await requestResult(db.transaction(TILES).objectStore(TILES).get([captureId, index]));
  } finally {
    db.close();
  }
}

export async function clearCaptureTiles(captureId) {
  const db = await openDatabase();
  try {
    const tx = db.transaction(TILES, "readwrite"), store = tx.objectStore(TILES);
    const keys = await requestResult(store.index("byCapture").getAllKeys(IDBKeyRange.only(captureId)));
    for (const key of keys) store.delete(key);
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve; tx.onerror = () => reject(tx.error); tx.onabort = () => reject(tx.error);
    });
  } finally { db.close(); }
}

// Delete all records finished before `cutoffIso`, their tiles, and any orphaned tiles
// whose parent record no longer exists. Returns the number of records removed.
export async function pruneOlderThan(cutoffIso, activeCaptureIds = []) {
  const records = await listRecords();
  const active = new Set(activeCaptureIds);
  const keep = new Set();
  let removed = 0;
  for (const record of records) {
    const stamp = record.finishedAt || record.startedAt || "";
    if (stamp && stamp < cutoffIso) {
      await deleteCapture(record.id);
      removed++;
    } else {
      keep.add(record.id);
    }
  }
  const db = await openDatabase();
  try {
    const tx = db.transaction(TILES, "readwrite");
    const store = tx.objectStore(TILES);
    const keys = await requestResult(store.getAllKeys());
    for (const key of keys) {
      const retryOwner = String(key[0]).split("-retry-")[0];
      if (!keep.has(key[0]) && !active.has(retryOwner)) store.delete(key);
    }
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
  return removed;
}

export async function deleteCapture(captureId) {
  const db = await openDatabase();
  try {
    const tx = db.transaction([RECORDS, TILES], "readwrite");
    tx.objectStore(RECORDS).delete(captureId);
    const index = tx.objectStore(TILES).index("byCapture");
    const keys = await requestResult(index.getAllKeys(IDBKeyRange.only(captureId)));
    for (const key of keys) tx.objectStore(TILES).delete(key);
    await new Promise((resolve, reject) => {
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
    });
  } finally {
    db.close();
  }
}

/** Replace a partial stitch only after its recapture succeeds. One transaction
 * keeps the old raster set intact if promotion fails or History deletes it. */
export async function promoteCaptureTiles(captureId, stagedId) {
  const db = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const tx = db.transaction([RECORDS, TILES], "readwrite");
      const tiles = tx.objectStore(TILES);
      let found = false;
      tx.oncomplete = () => resolve(found);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error);
      const record = tx.objectStore(RECORDS).get(captureId);
      record.onsuccess = () => {
        if (!record.result) return;
        found = true;
        const keys = tiles.index("byCapture").getAllKeys(IDBKeyRange.only(captureId));
        keys.onsuccess = () => {
          for (const key of keys.result) tiles.delete(key);
          const cursor = tiles.index("byCapture").openCursor(IDBKeyRange.only(stagedId));
          cursor.onsuccess = () => {
            const next = cursor.result;
            if (!next) return;
            tiles.put({ ...next.value, captureId });
            next.delete();
            next.continue();
          };
        };
      };
    });
  } finally { db.close(); }
}
