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

// Delete all records finished before `cutoffIso`, their tiles, and any orphaned tiles
// whose parent record no longer exists. Returns the number of records removed.
export async function pruneOlderThan(cutoffIso) {
  const records = await listRecords();
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
      if (!keep.has(key[0])) store.delete(key);
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
