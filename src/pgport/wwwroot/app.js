"use strict";

const $ = (id) => document.getElementById(id);
const els = {
  database: $("database"), sql: $("sql"), run: $("run"), cancel: $("cancel"),
  pageSize: $("pageSize"), downloadData: $("downloadData"), downloadFormat: $("downloadFormat"), status: $("status"),
  error: $("error"), tableWrap: $("tableWrap"), table: $("results"),
  pager: $("pager"), prev: $("prev"), next: $("next"), pageLabel: $("pageLabel"),
  save: $("save"), savedLabel: $("savedLabel"), toggleSidebar: $("toggleSidebar"),
  tabs: document.querySelectorAll(".tabs [role=tab]"), panels: document.querySelectorAll(".tab-panel"),
  schemaFilter: $("schemaFilter"), schemaRefresh: $("schemaRefresh"), schemaStatus: $("schemaStatus"), schemaTree: $("schemaTree"),
  savedFilter: $("savedFilter"), savedStatus: $("savedStatus"), savedList: $("savedList"),
  historyFilter: $("historyFilter"), historyClear: $("historyClear"), historyList: $("historyList"),
  saveDialog: $("saveDialog"), saveName: $("saveName"), saveDb: $("saveDb"), saveError: $("saveError"),
  saveCancel: $("saveCancel"), saveAsNew: $("saveAsNew"), saveUpdate: $("saveUpdate"),
};

// The query currently shown in the grid. Paging re-runs *this*, not whatever is in the editor now.
let current = null; // { database, sql, page }
let inFlight = null; // AbortController

let activeTab = "tables";
let savedQueries = []; // from the server, sorted by name
let loadedSavedId = null; // the saved query that was last opened in (or saved from) the editor
let history = [];
const HISTORY_KEY = "dx.history";
const HISTORY_MAX = 200;

const schemaCache = new Map(); // database -> Promise<{ schemas } | { error }>
let schemaShown = null; // schemas of the selected database, once loaded
const expandedTables = new Set(); // "schema.table"
const collapsedSchemas = new Set();

const store = {
  get(key) { try { return localStorage.getItem(key); } catch { return null; } },
  set(key, value) { try { localStorage.setItem(key, value); } catch { /* ignore */ } },
};

async function init() {
  els.sql.value = store.get("dx.sql") ?? "";
  loadedSavedId = store.get("dx.savedId") || null;
  history = loadHistory();
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
  els.database.addEventListener("change", onDatabaseChanged);
  els.sql.addEventListener("input", onEditorChanged);
  els.sql.addEventListener("keydown", onEditorKeyDown);

  // Sidebar
  const sidebarPref = store.get("dx.sidebar");
  setSidebar(sidebarPref ? sidebarPref === "open" : matchMedia("(min-width: 801px)").matches, false);
  els.toggleSidebar.addEventListener("click", () => setSidebar(document.body.classList.contains("sidebar-closed")));
  els.tabs.forEach((tab) => tab.addEventListener("click", () => showTab(tab.dataset.tab)));
  els.schemaFilter.addEventListener("input", renderSchema);
  els.schemaRefresh.addEventListener("click", () => loadSchema(true));
  els.savedFilter.addEventListener("input", renderSaved);
  els.historyFilter.addEventListener("input", renderHistory);
  els.historyClear.addEventListener("click", clearHistory);

  // Saving
  els.save.addEventListener("click", openSaveDialog);
  els.saveCancel.addEventListener("click", () => els.saveDialog.close());
  els.saveAsNew.addEventListener("click", () => saveQuery("new"));
  els.saveUpdate.addEventListener("click", () => saveQuery("update"));
  els.saveName.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      saveQuery(els.saveUpdate.hidden ? "new" : "update");
    }
  });
  document.addEventListener("keydown", (e) => {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "s") {
      e.preventDefault();
      if (!els.saveDialog.open) openSaveDialog();
    }
  });

  renderHistory();
  await loadDatabases();
  showTab(store.get("dx.tab") ?? "tables");
  loadSavedQueries();
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
    onEditorChanged();
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
  execute({ record: true });
}

function goToPage(page) {
  if (!current || page < 1) return;
  current = { ...current, page };
  execute();
}

/** Runs `current`. `record` adds the run to history (fresh runs only, not paging). */
async function execute({ record = false } = {}) {
  inFlight?.abort();
  const controller = new AbortController();
  inFlight = controller;
  setBusy(true);
  clearError();
  els.status.textContent = "Running…";

  const pageSize = Number(els.pageSize.value);
  const ran = { sql: current.sql, database: current.database, at: new Date().toISOString() };
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
      if (record) addHistory({ ...ran, ok: false, error: body.error });
      return;
    }
    render(body);
    if (record) addHistory({ ...ran, ok: true, rows: body.rows.length, hasMore: body.hasMore, ms: body.elapsedMs });
  } catch (e) {
    if (e.name === "AbortError") {
      els.status.textContent = "Cancelled.";
    } else {
      showError({ error: e.message });
      els.status.textContent = "";
      if (record) addHistory({ ...ran, ok: false, error: e.message });
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

// ---------------------------------------------------------------------------
// Editor helpers
// ---------------------------------------------------------------------------

function onEditorChanged() {
  store.set("dx.sql", els.sql.value);
  updateSavedLabel();
}

function onDatabaseChanged() {
  store.set("dx.database", els.database.value);
  schemaShown = null;
  if (activeTab === "tables") loadSchema();
}

/** Replaces the editor contents; `savedId` marks it as an opened saved query. */
function setEditor(sql, savedId = null) {
  els.sql.value = sql;
  setLoadedSaved(savedId);
  onEditorChanged();
  els.sql.focus();
}

/** Inserts at the cursor, adding spaces so the text doesn't run into a neighbouring word. */
function insertIntoEditor(text) {
  const { selectionStart, selectionEnd, value } = els.sql;
  const touches = (ch) => /[\w$"]/.test(ch ?? "");
  if (touches(value[selectionStart - 1])) text = " " + text;
  if (touches(value[selectionEnd])) text += " ";
  els.sql.setRangeText(text, selectionStart, selectionEnd, "end");
  els.sql.focus();
  onEditorChanged();
}

/** True if replacing the editor won't lose work: it's empty, saved, or in history. */
function confirmReplaceEditor() {
  const text = els.sql.value.trim();
  if (!text) return true;
  if (savedQueries.some((q) => q.sql.trim() === text)) return true;
  if (history.some((h) => h.sql.trim() === text)) return true;
  return confirm("Replace the query in the editor? It hasn't been run or saved.");
}

function selectDatabase(name) {
  if (!name || name === els.database.value) return;
  if (![...els.database.options].some((o) => o.value === name)) return;
  els.database.value = name;
  onDatabaseChanged();
}

// Postgres reserved words that must be quoted when used as identifiers.
const RESERVED = new Set(("all analyse analyze and any array as asc asymmetric both case cast check collate column "
  + "constraint create current_catalog current_date current_role current_time current_timestamp current_user default "
  + "deferrable desc distinct do else end except false fetch for foreign from grant group having in initially "
  + "intersect into lateral leading limit localtime localtimestamp not null offset on only or order placing primary "
  + "references returning select session_user some symmetric system_user table then to trailing true union unique "
  + "user using variadic when where window with").split(" "));

function quoteIdent(name) {
  return /^[a-z_][a-z0-9_$]*$/.test(name) && !RESERVED.has(name) ? name : `"${name.replaceAll('"', '""')}"`;
}

/** `public` is on the default search_path, so its tables don't need a schema prefix. */
const qualifiedName = (schema, table) => (schema === "public" ? "" : quoteIdent(schema) + ".") + quoteIdent(table);

// ---------------------------------------------------------------------------
// Sidebar
// ---------------------------------------------------------------------------

function setSidebar(open, remember = true) {
  document.body.classList.toggle("sidebar-closed", !open);
  els.toggleSidebar.setAttribute("aria-expanded", String(open));
  if (remember) store.set("dx.sidebar", open ? "open" : "closed");
}

function showTab(name) {
  if (![...els.tabs].some((t) => t.dataset.tab === name)) name = "tables";
  activeTab = name;
  store.set("dx.tab", name);
  els.tabs.forEach((t) => t.setAttribute("aria-selected", String(t.dataset.tab === name)));
  els.panels.forEach((p) => { p.hidden = p.dataset.panel !== name; });
  if (name === "tables" && !schemaShown) loadSchema();
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = text;
  return node;
}

function button(className, text, label, onClick) {
  const b = el("button", className, text);
  b.type = "button";
  if (label) {
    b.title = label;
    b.setAttribute("aria-label", label);
  }
  b.addEventListener("click", onClick);
  return b;
}

function setPanelStatus(target, message, isError = false) {
  target.textContent = message;
  target.classList.toggle("error-text", isError);
}

function errorText(err) {
  return [err?.error ?? err?.message ?? "Something went wrong.", err?.hint && "Hint: " + err.hint].filter(Boolean).join("\n");
}

/** Short one-line preview of a query for list items. */
function sqlPreview(sql) {
  const flat = sql.trim().replace(/\s+/g, " ");
  return flat.length > 200 ? flat.slice(0, 200) + "…" : flat;
}

function when(iso) {
  const d = new Date(iso);
  const now = new Date();
  const time = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  if (d.toDateString() === now.toDateString()) return time;
  const date = d.toLocaleDateString([], {
    month: "short", day: "numeric", ...(d.getFullYear() !== now.getFullYear() && { year: "numeric" }),
  });
  return `${date} ${time}`;
}

const compact = new Intl.NumberFormat(undefined, { notation: "compact", maximumFractionDigits: 1 });

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = res.status === 204 ? null : await readJson(res);
  if (!res.ok) throw Object.assign(new Error(data?.error ?? `HTTP ${res.status}`), { api: data, status: res.status });
  return data;
}

// ---------------------------------------------------------------------------
// Tables (schema browser)
// ---------------------------------------------------------------------------

async function loadSchema(force = false) {
  const database = els.database.value;
  if (!database) return;
  if (force) schemaCache.delete(database);

  if (!schemaCache.has(database)) {
    schemaCache.set(database, api("GET", "api/schema?database=" + encodeURIComponent(database))
      .then((schemas) => ({ schemas }), (e) => ({ error: e.api ?? { error: e.message } })));
  }
  setPanelStatus(els.schemaStatus, "Loading…");
  if (force || !schemaShown) els.schemaTree.replaceChildren();

  const result = await schemaCache.get(database);
  if (database !== els.database.value) return; // switched databases while loading

  if (result.error) {
    schemaCache.delete(database); // retry on next show
    schemaShown = null;
    els.schemaTree.replaceChildren();
    setPanelStatus(els.schemaStatus, errorText(result.error), true);
    return;
  }
  schemaShown = result.schemas;
  renderSchema();
}

function renderSchema() {
  if (!schemaShown) return;
  const filter = els.schemaFilter.value.trim().toLowerCase();
  const fragment = document.createDocumentFragment();
  let total = 0;
  let shown = 0;

  for (const schema of schemaShown) {
    total += schema.tables.length;
    const tables = filter
      ? schema.tables.filter((t) => `${schema.name}.${t.name}`.toLowerCase().includes(filter))
      : schema.tables;
    if (tables.length === 0) continue;
    shown += tables.length;
    fragment.append(schemaNode(schema.name, tables, !filter && collapsedSchemas.has(schema.name)));
  }

  els.schemaTree.replaceChildren(fragment);
  const noun = (n) => `${fmt(n)} ${n === 1 ? "table" : "tables"}`;
  setPanelStatus(els.schemaStatus,
    total === 0 ? "No tables or views found."
      : filter ? `${fmt(shown)} of ${noun(total)} match`
      : `${noun(total)} in ${fmt(schemaShown.length)} ${schemaShown.length === 1 ? "schema" : "schemas"}`);
}

function schemaNode(name, tables, collapsed) {
  const li = el("li", "schema");
  const list = el("ul");
  list.append(...tables.map((t) => tableNode(name, t)));
  list.hidden = collapsed;

  const head = button("schema-name", null, null, () => {
    list.hidden = !list.hidden;
    head.setAttribute("aria-expanded", String(!list.hidden));
    if (list.hidden) collapsedSchemas.add(name); else collapsedSchemas.delete(name);
  });
  head.setAttribute("aria-expanded", String(!collapsed));
  head.append(el("span", "label", name), el("span", "count", fmt(tables.length)));

  li.append(head, list);
  return li;
}

const KIND_BADGE = { "table": "T", "view": "V", "materialized view": "MV", "foreign table": "FT" };

function tableNode(schema, table) {
  const key = `${schema}.${table.name}`;
  const name = qualifiedName(schema, table.name);
  const li = el("li", table.canSelect ? "table" : "table no-access");
  const row = el("div", "table-row");
  let columns = null;

  const setOpen = (open) => {
    toggle.setAttribute("aria-expanded", String(open));
    if (open) {
      expandedTables.add(key);
      columns ??= columnList(table);
      li.append(columns);
    } else {
      expandedTables.delete(key);
      columns?.remove();
    }
  };

  const toggle = button("table-name", null, null, () => setOpen(!expandedTables.has(key)));
  toggle.title = `${key} (${table.kind})` + (table.canSelect ? "" : "\nThis role has no SELECT permission on it.");
  toggle.append(el("span", "kind", KIND_BADGE[table.kind] ?? "?"), el("span", "label", table.name));
  if (table.estimatedRows != null) {
    const rows = el("span", "rows", "≈" + compact.format(table.estimatedRows));
    rows.title = `About ${fmt(table.estimatedRows)} rows (planner estimate)`;
    toggle.append(rows);
  }

  row.append(
    toggle,
    button("icon-btn", "↳", `Insert ${name} into the editor`, () => insertIntoEditor(name)),
    button("icon-btn", "▶", `Query ${name}`, () => queryTable(name)),
  );
  li.append(row);
  if (expandedTables.has(key)) setOpen(true);
  return li;
}

function columnList(table) {
  const ul = el("ul", "columns");
  if (table.columns.length === 0) ul.append(el("li", "empty", "No columns"));
  for (const column of table.columns) {
    const li = el("li");
    const b = button("column", null, null, () => insertIntoEditor(quoteIdent(column.name)));
    b.title = `${column.name} ${column.type}${column.notNull ? " NOT NULL" : ""}\nClick to insert into the editor`;
    b.append(el("span", "label", column.name), el("span", "type", column.type + (column.notNull ? "" : "?")));
    li.append(b);
    ul.append(li);
  }
  return ul;
}

function queryTable(name) {
  if (!confirmReplaceEditor()) return;
  setEditor(`SELECT *\nFROM ${name};`);
  runFromEditor();
}

// ---------------------------------------------------------------------------
// Saved queries (stored on the server)
// ---------------------------------------------------------------------------

async function loadSavedQueries() {
  try {
    savedQueries = await api("GET", "api/saved-queries");
    setPanelStatus(els.savedStatus, "");
  } catch (e) {
    setPanelStatus(els.savedStatus, "Couldn't load saved queries: " + errorText(e.api ?? e), true);
  }
  renderSaved();
  updateSavedLabel();
}

function setLoadedSaved(id) {
  loadedSavedId = id;
  store.set("dx.savedId", id ?? "");
  updateSavedLabel();
  renderSaved();
}

function renderSaved() {
  const filter = els.savedFilter.value.trim().toLowerCase();
  const items = savedQueries.filter((q) => !filter
    || q.name.toLowerCase().includes(filter) || q.sql.toLowerCase().includes(filter));

  els.savedList.replaceChildren(...items.map(savedNode));
  if (savedQueries.length === 0) {
    els.savedList.append(el("li", "empty", "No saved queries yet. Write a query and press Save (Ctrl+S)."));
  } else if (items.length === 0) {
    els.savedList.append(el("li", "empty", "No saved queries match."));
  }
}

function savedNode(q) {
  const li = el("li", q.id === loadedSavedId ? "item active" : "item");
  const open = button("item-main", null, null, () => openSaved(q));
  open.title = q.sql.length > 1500 ? q.sql.slice(0, 1500) + "\n…" : q.sql;
  open.append(
    el("span", "item-title", q.name),
    el("span", "item-meta", [q.database, "updated " + when(q.updatedAt)].filter(Boolean).join(" · ")),
    el("code", "item-sql", sqlPreview(q.sql)),
  );
  li.append(open, button("item-action", "×", `Delete “${q.name}”`, () => deleteSaved(q)));
  return li;
}

function openSaved(q) {
  if (!confirmReplaceEditor()) return;
  setEditor(q.sql, q.id);
  selectDatabase(q.database);
}

async function deleteSaved(q) {
  if (!confirm(`Delete the saved query “${q.name}”? This can't be undone.`)) return;
  try {
    await api("DELETE", `api/saved-queries/${encodeURIComponent(q.id)}`);
  } catch (e) {
    if (e.status !== 404) { // already gone is fine
      setPanelStatus(els.savedStatus, "Couldn't delete: " + errorText(e.api ?? e), true);
      return;
    }
  }
  if (loadedSavedId === q.id) setLoadedSaved(null);
  await loadSavedQueries();
}

function updateSavedLabel() {
  const q = savedQueries.find((s) => s.id === loadedSavedId);
  els.savedLabel.hidden = !q;
  if (!q) return;
  const edited = els.sql.value !== q.sql;
  els.savedLabel.textContent = (edited ? "● " : "") + q.name;
  els.savedLabel.title = edited ? `Edited since “${q.name}” was saved. Press Save to update it.` : `Saved query “${q.name}”`;
  els.savedLabel.classList.toggle("edited", edited);
}

function openSaveDialog() {
  if (!els.sql.value.trim()) {
    showError({ error: "There's no SQL to save." });
    return;
  }
  const q = savedQueries.find((s) => s.id === loadedSavedId);
  els.saveName.value = q?.name ?? leadingComment(els.sql.value);
  els.saveDb.textContent = els.database.value || "(none)";
  els.saveUpdate.hidden = !q;
  if (q) els.saveUpdate.textContent = `Update “${q.name}”`;
  els.saveAsNew.classList.toggle("primary", !q);
  els.saveError.hidden = true;
  els.saveDialog.showModal();
  els.saveName.select();
}

/** Suggests a name from a leading `-- comment`, if the query has one. */
function leadingComment(sql) {
  const match = /^\s*--\s*(.+)/.exec(sql);
  return match ? match[1].trim().slice(0, 200) : "";
}

async function saveQuery(mode) {
  const name = els.saveName.value.trim();
  if (!name) {
    showSaveError({ error: "Give the query a name." });
    els.saveName.focus();
    return;
  }

  const payload = { name, database: els.database.value || null, sql: els.sql.value };
  const buttons = [els.saveAsNew, els.saveUpdate];
  buttons.forEach((b) => { b.disabled = true; });
  try {
    const saved = mode === "update" && loadedSavedId
      ? await api("PUT", `api/saved-queries/${encodeURIComponent(loadedSavedId)}`, payload)
      : await api("POST", "api/saved-queries", payload);
    els.saveDialog.close();
    setLoadedSaved(saved.id);
    await loadSavedQueries();
  } catch (e) {
    showSaveError(e.api ?? { error: e.message });
  } finally {
    buttons.forEach((b) => { b.disabled = false; });
  }
}

function showSaveError(err) {
  els.saveError.textContent = errorText(err);
  els.saveError.hidden = false;
}

// ---------------------------------------------------------------------------
// History (stored in this browser)
// ---------------------------------------------------------------------------

function loadHistory() {
  try {
    const parsed = JSON.parse(store.get(HISTORY_KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((h) => typeof h?.sql === "string") : [];
  } catch {
    return [];
  }
}

function saveHistory() {
  store.set(HISTORY_KEY, JSON.stringify(history));
}

/** Newest first. Re-running the same query on the same database moves it to the top. */
function addHistory(entry) {
  if (!entry.sql.trim()) return;
  const same = (h) => h.database === entry.database && h.sql.trim() === entry.sql.trim();
  history = [entry, ...history.filter((h) => !same(h))].slice(0, HISTORY_MAX);
  saveHistory();
  renderHistory();
}

function clearHistory() {
  if (!confirm("Clear all query history in this browser?")) return;
  history = [];
  saveHistory();
  renderHistory();
}

function renderHistory() {
  const filter = els.historyFilter.value.trim().toLowerCase();
  const items = history.filter((h) => !filter
    || h.sql.toLowerCase().includes(filter) || (h.database ?? "").toLowerCase().includes(filter));

  els.historyList.replaceChildren(...items.map(historyNode));
  if (history.length === 0) {
    els.historyList.append(el("li", "empty", "Queries you run will show up here."));
  } else if (items.length === 0) {
    els.historyList.append(el("li", "empty", "No history matches."));
  }
  els.historyClear.disabled = history.length === 0;
}

function historyNode(h) {
  const li = el("li", h.ok ? "item" : "item failed");
  const outcome = h.ok
    ? `${fmt(h.rows)}${h.hasMore ? "+" : ""} ${h.rows === 1 && !h.hasMore ? "row" : "rows"} · ${fmt(h.ms)} ms`
    : "failed";
  const open = button("item-main", null, null, () => openHistory(h));
  const sqlTitle = h.sql.length > 1500 ? h.sql.slice(0, 1500) + "\n…" : h.sql;
  open.title = h.ok ? sqlTitle : `${h.error ?? "Failed"}\n\n${sqlTitle}`;
  open.append(
    el("span", "item-meta", [when(h.at), h.database, outcome].filter(Boolean).join(" · ")),
    el("code", "item-sql", sqlPreview(h.sql)),
  );
  li.append(open, button("item-action", "×", "Remove from history", () => {
    history = history.filter((x) => x !== h);
    saveHistory();
    renderHistory();
  }));
  return li;
}

function openHistory(h) {
  if (!confirmReplaceEditor()) return;
  setEditor(h.sql);
  selectDatabase(h.database);
}

// Last, so every const above is initialized before init() touches it.
init();
