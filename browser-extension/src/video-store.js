// Videos have their own database: listing history never loads the media blobs.
const DB_NAME = "winshot-video";
let connection;
function database() {
  if (!connection) connection = new Promise((resolve, reject) => {
    const request = indexedDB.open(DB_NAME, 1);
    request.onupgradeneeded = () => {
      const db = request.result;
      db.createObjectStore("records", { keyPath: "id" });
      db.createObjectStore("media");
    };
    request.onsuccess = () => {
      const db = request.result;
      db.onversionchange = () => { db.close(); connection = undefined; };
      resolve(db);
    };
    request.onerror = () => { connection = undefined; reject(request.error); };
    request.onblocked = () => { connection = undefined; reject(new Error("Close other WinShot video tabs and try again.")); };
  });
  return connection;
}
function requestResult(request) {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
  });
}
function transactionDone(transaction) {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = resolve;
    transaction.onabort = transaction.onerror = () => reject(transaction.error || new Error("Video could not be saved."));
  });
}
export async function saveVideo(blob, metadata) {
  if (!(blob instanceof Blob) || !blob.size) throw new Error("The recording is empty.");
  const db = await database();
  const transaction = db.transaction(["records", "media"], "readwrite");
  const done = transactionDone(transaction);
  const record = { ...metadata, id: metadata.id || crypto.randomUUID(), createdAt: metadata.createdAt || new Date().toISOString(), size: blob.size, mimeType: blob.type };
  try {
    transaction.objectStore("records").put(record);
    transaction.objectStore("media").put(blob, record.id);
  } catch (error) {
    transaction.abort();
    await done.catch(() => {});
    throw error;
  }
  await done;
  return record;
}
export async function listVideos() {
  const db = await database();
  return (await requestResult(db.transaction("records").objectStore("records").getAll()))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}
export async function getVideo(id) {
  const db = await database();
  const transaction = db.transaction(["records", "media"]);
  const [record, blob] = await Promise.all([
    requestResult(transaction.objectStore("records").get(id)),
    requestResult(transaction.objectStore("media").get(id))
  ]);
  return record && blob ? { record, blob } : null;
}
export async function updateVideo(id, updates) {
  const db = await database();
  const transaction = db.transaction("records", "readwrite");
  const done = transactionDone(transaction);
  const store = transaction.objectStore("records");
  const request = store.get(id);
  request.onsuccess = () => { if (request.result) store.put({ ...request.result, ...updates, id }); };
  await done;
}
export async function deleteVideo(id) {
  const db = await database();
  const transaction = db.transaction(["records", "media"], "readwrite");
  const done = transactionDone(transaction);
  transaction.objectStore("records").delete(id);
  transaction.objectStore("media").delete(id);
  await done;
}
