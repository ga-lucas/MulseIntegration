// Browser-local storage for BizTalk import sessions. Nothing here leaves the browser.
// The index (options, evidence list, answers) lives in localStorage. Pasted and uploaded evidence content lives in IndexedDB,
// because it can be much larger than localStorage allows. Content is returned to .NET in slices: the Blazor circuit accepts
// only about 32 KB per message from the browser. Keys are "<session>/<id>"; session names never contain "/".
const indexKey = "mulse.biztalk-import.sessions.v1";
const databaseName = "mulse-biztalk-import";
const storeName = "evidence";

const opened = new Map();

function readIndex() {
    try {
        const parsed = JSON.parse(localStorage.getItem(indexKey) ?? "{}");
        return parsed && typeof parsed === "object" ? parsed : {};
    } catch {
        return {};
    }
}

export function listSessions() {
    return Object.entries(readIndex()).map(([name, json]) => {
        try {
            return { name, updatedUtc: JSON.parse(json).updatedUtc ?? null };
        } catch {
            return { name, updatedUtc: null };
        }
    });
}

export function loadIndex(name) {
    return readIndex()[name] ?? null;
}

export function saveIndex(name, json) {
    const index = readIndex();
    index[name] = json;
    localStorage.setItem(indexKey, JSON.stringify(index));
}

export function deleteIndex(name) {
    const index = readIndex();
    delete index[name];
    localStorage.setItem(indexKey, JSON.stringify(index));
}

function openDatabase() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(databaseName, 1);
        request.onupgradeneeded = () => request.result.createObjectStore(storeName);
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error("The browser storage is blocked by another tab."));
    });
}

async function run(mode, action) {
    const database = await openDatabase();
    try {
        return await new Promise((resolve, reject) => {
            const transaction = database.transaction(storeName, mode);
            const result = action(transaction.objectStore(storeName));
            transaction.oncomplete = () => resolve(result?.result ?? null);
            transaction.onerror = () => reject(transaction.error);
            transaction.onabort = () => reject(transaction.error);
        });
    } finally {
        database.close();
    }
}

const keyOf = (session, id) => `${session}/${id}`;

export function putContent(session, id, data) {
    return run("readwrite", store => store.put(data, keyOf(session, id)));
}

// Returns the stored length in characters, or null when nothing is stored. The data stays in memory until releaseContent.
export async function openContent(session, id) {
    const key = keyOf(session, id);
    const data = await run("readonly", store => store.get(key));
    if (typeof data !== "string") return null;
    opened.set(key, data);
    return data.length;
}

export function readContentSlice(session, id, offset, length) {
    const data = opened.get(keyOf(session, id));
    if (data === undefined) throw new Error("The content is not open.");
    return data.substring(offset, offset + length);
}

export function releaseContent(session, id) {
    opened.delete(keyOf(session, id));
}

export function deleteContent(session, id) {
    return run("readwrite", store => store.delete(keyOf(session, id)));
}

export function deleteSessionContent(session) {
    return run("readwrite", store => store.delete(IDBKeyRange.bound(`${session}/`, `${session}/\uffff`)));
}
