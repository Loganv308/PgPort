"use strict";

const $ = (id) => document.getElementById(id);
const els = {
  database: $("database"), sql: $("sql"), run: $("run"), cancel: $("cancel"),
  pageSize: $("pageSize"), downloadData: $("downloadData"), downloadFormat: $("downloadFormat"), status: $("status"),
  error: $("error"), tableWrap: $("tableWrap"), table: $("results"),
  pager: $("pager"), prev: $("prev"), next: $("next"), pageLabel: $("pageLabel"),
};

// The query currently shown in the grid. Paging re-runs *this*, not whatever is in the editor now.
let current = null; // { database, sql, page }
let inFlight = null; // AbortController

const store = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } },
};

init();

async function init() {
  els.sql.value = store.get("dx.sql") ?? "";
  const savedSize = store.get("dx.pageSize");
  if (savedSize) els.pageSize.value = savedSize;

  els.run.addEventListener("click", () => runFromEditor());
  els.cancel.addEventListener("click", () => inFlight?.abort());
  els.prev.addEventListener("click", () => goToPage(current.page - 1));
  els.next.addEventListener("click", () => goToPage(current.page + 1));
  els.downloadData.addEventListener("click", () => downloadData(els.downloadFormat.value));
  els.pageSize.addEventListener("change", () => {
    store.set("dx.pageSize", els.pageSize.value);
    if (current) goToPage(1);
  });
  els.database.addEventListener("change", () => store.set("dx.database", els.database.value));
  els.sql.addEventListener("input", () => store.set("dx.sql", els.sql.value));
  els.sql.addEventListener("keydown", onEditorKeyDown);

  await loadDatabases();
}

async function loadDatabases() {
  try {
    const res = await fetch("api/databases");
    const names = await res.json();
    els.database.replaceChildren(...names.map((n) => new Option(n, n)));
    if (names.length === 0) {
      showError({ error: "No databases are configured.", hint: "Set Databases__<Name>=<connection string> on the container and restart it." });
      setBusy(false, true);
      return;
    }
    const saved = store.get("dx.database");
    if (saved && names.includes(saved)) els.database.value = saved;
  } catch (e) {
    showError({ error: "Couldn't load the database list: " + e.message });
  }
}

function onEditorKeyDown(e) {
  if ((e.ctrlKey || e.metaKey) && e.key === "Enter") {
    e.preventDefault();
    runFromEditor();
  } else if (e.key === "Tab" && !e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
    e.preventDefault();
    els.sql.setRangeText("  ", els.sql.selectionStart, els.sql.selectionEnd, "end");
    store.set("dx.sql", els.sql.value);
  }
}

/** The highlighted text if there is a selection, otherwise the whole editor. */
function editorSql() {
  const { selectionStart: s, selectionEnd: e, value } = els.sql;
  const selected = s !== e ? value.slice(s, e) : "";
  return selected.trim() ? selected : value;
}

function runFromEditor() {
  current = { database: els.database.value, sql: editorSql(), page: 1 };
  execute();
}

function goToPage(page) {
  if (!current || page < 1) return;
  current = { ...current, page };
  execute();
}

async function execute() {
  inFlight?.abort();
  const controller = new AbortController();
  inFlight = controller;
  setBusy(true);
  clearError();
  els.status.textContent = "Running…";

  const pageSize = Number(els.pageSize.value);
  try {
    const res = await fetch("api/query", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ database: current.database, sql: current.sql, page: current.page, pageSize }),
      signal: controller.signal,
    });
    const body = await readJson(res);
    if (!res.ok) {
      showError(body);
      els.status.textContent = "";
      return;
    }
    render(body);
  } catch (e) {
    if (e.name === "AbortError") {
      els.status.textContent = "Cancelled.";
    } else {
      showError({ error: e.message });
      els.status.textContent = "";
    }
  } finally {
    if (inFlight === controller) {
      inFlight = null;
      setBusy(false);
    }
  }
}

function render(result) {
  const { columns, rows, page, pageSize, hasMore, paged, elapsedMs } = result;
  current.page = page;

  const first = (page - 1) * pageSize + 1;
  const last = first + rows.length - 1;
  const where = current.database ? ` · ${current.database}` : "";
  els.status.textContent = rows.length === 0
    ? `No rows${page > 1 ? " on this page" : ""} · ${elapsedMs} ms${where}`
    : `Rows ${fmt(first)}–${fmt(last)}${hasMore ? "" : ` of ${fmt(last)}`} · ${elapsedMs} ms${where}`
      + (!paged && hasMore ? " · showing the first page only for this statement type" : "");

  const thead = document.createElement("thead");
  const headRow = thead.insertRow();
  headRow.appendChild(th("#", null, "rownum"));
  columns.forEach((c) => headRow.appendChild(th(c.name, c.type)));

  const numeric = columns.map((c) => /^(int|smallint|bigint|integer|numeric|real|double|decimal|float|money|oid)/i.test(c.type));

  const tbody = document.createElement("tbody");
  rows.forEach((row, r) => {
    const tr = tbody.insertRow();
    const num = tr.insertCell();
    num.className = "rownum";
    num.textContent = fmt(first + r);
    row.forEach((value, i) => {
      const td = tr.insertCell();
      if (value === null) {
        td.className = "null";
        td.textContent = "NULL";
        return;
      }
      const text = typeof value === "object" ? JSON.stringify(value) : String(value);
      td.textContent = text;
      if (text.length > 40) td.title = text;
      if (numeric[i]) td.classList.add("num");
    });
  });

  els.table.replaceChildren(thead, tbody);
  els.tableWrap.hidden = columns.length === 0;
  els.tableWrap.scrollTop = 0;

  els.pager.hidden = !paged || (page === 1 && !hasMore);
  els.prev.disabled = page <= 1;
  els.next.disabled = !hasMore;
  els.pageLabel.textContent = `Page ${fmt(page)}`;
}

function th(label, type, className) {
  const cell = document.createElement("th");
  if (className) cell.className = className;
  cell.textContent = label;
  if (type) {
    const small = document.createElement("small");
    small.textContent = type;
    cell.appendChild(small);
  }
  return cell;
}

async function downloadData(format) {
  const sql = editorSql();
  const database = els.database.value;
  clearError();
  els.downloadData.disabled = true;
  const previous = els.status.textContent;
  els.status.textContent = "Preparing export…";
  try {
    const res = await fetch(`api/export/${format.toLowerCase()}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ database, sql }),
    });
    const body = await readJson(res);
    if (!res.ok) {
      showError(body);
      els.status.textContent = previous;
      return;
    }
    // Plain navigation to the one-time URL lets the browser stream the file to disk.
    const a = document.createElement("a");
    a.href = body.url.replace(/^\//, "");
    a.download = "";
    document.body.appendChild(a);
    a.click();
    a.remove();
    els.status.textContent = "Download started — check your browser's downloads.";
  } catch (e) {
    showError({ error: e.message });
    els.status.textContent = previous;
  } finally {
    els.downloadData.disabled = false;
  }
}

async function readJson(res) {
  const text = await res.text();
  try { return JSON.parse(text); } catch { return { error: text || `HTTP ${res.status}` }; }
}

function showError(err) {
  els.error.replaceChildren();
  const lines = [err.error ?? "Something went wrong."];
  if (err.line) lines[0] += ` (line ${err.line}, column ${err.column})`;
  if (err.detail) lines.push(err.detail);
  if (err.hint) lines.push("Hint: " + err.hint);
  els.error.append(lines.join("\n"));
  if (err.near) {
    const near = document.createElement("span");
    near.className = "near";
    near.textContent = err.near + "\n" + " ".repeat(Math.max(0, (err.column ?? 1) - 1)) + "^";
    els.error.append(near);
  }
  els.error.hidden = false;
}

function clearError() {
  els.error.hidden = true;
  els.error.replaceChildren();
}

function setBusy(busy, disableAll = false) {
  els.run.disabled = busy || disableAll;
  els.downloadData.disabled = busy || disableAll;
  els.cancel.hidden = !busy;
  els.prev.disabled = busy || !current || current.page <= 1;
  els.next.disabled = busy || els.next.disabled;
}

const fmt = (n) => Number(n).toLocaleString();
