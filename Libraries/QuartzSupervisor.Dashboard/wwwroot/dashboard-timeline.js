const timelines = new WeakMap();
let libraryPromise;
let stylesheetPromise;

async function loadLibrary(data) {
    stylesheetPromise ??= new Promise((resolve, reject) => {
        const link = document.createElement("link");
        link.rel = "stylesheet";
        link.href = data.styleUrl;
        link.onload = resolve;
        link.onerror = () => { link.remove(); stylesheetPromise = undefined; reject(new Error("Timeline stylesheet unavailable.")); };
        document.head.append(link);
    });
    if (window.vis?.Timeline) { await stylesheetPromise; return window.vis; }
    libraryPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = data.scriptUrl;
        script.onload = () => resolve(window.vis);
        script.onerror = () => { script.remove(); libraryPromise = undefined; reject(new Error("Timeline library unavailable.")); };
        document.head.append(script);
    });
    const [vis] = await Promise.all([libraryPromise, stylesheetPromise]);
    return vis;
}

function safeUrl(value) {
    const url = new URL(value, document.baseURI);
    return url.origin === location.origin && ["http:", "https:"].includes(url.protocol) ? url.href : null;
}

function groupLabel(group) {
    const label = document.createElement("div");
    label.className = "quartz-timeline-group";
    const link = document.createElement("a");
    link.textContent = group.name;
    const url = safeUrl(group.url);
    if (url) link.href = url;
    const detail = document.createElement("span");
    detail.textContent = group.detail;
    label.append(link, detail);
    return label;
}

function updateDataSet(set, values) {
    const ids = new Set(values.map(value => value.id));
    const removed = set.getIds().filter(id => !ids.has(id));
    if (removed.length) set.remove(removed);
    if (values.length) set.update(values);
}

function itemData(item) {
    const title = document.createElement("span");
    title.textContent = item.title;
    return { ...item, subgroup: item.type === "point" ? "forecast" : "runs", title };
}

function restartClock(state) {
    clearInterval(state.clock);
    state.clock = null;
    if (!state.visible || !state.intersecting || document.hidden || state.motion.matches || state.destroyed) return;
    state.clock = setInterval(() => {
        const now = new Date(state.captured + performance.now() - state.received);
        state.timeline.setCustomTime(now, "now");
        const running = state.items.get({ filter: item => item.running });
        if (running.length) state.items.update(running.map(item => ({ id: item.id, end: now })));
        if (state.following) {
            const range = state.timeline.getWindow();
            const half = (range.end - range.start) / 2;
            state.timeline.setWindow(new Date(now.getTime() - half), new Date(now.getTime() + half), { animation: false });
        }
    }, 1000);
}

function createTimeline(element, data, vis, callback) {
    const items = new vis.DataSet();
    const groups = new vis.DataSet();
    const timeline = new vis.Timeline(element, items, groups, {
        start: new Date(data.start), end: new Date(data.end),
        minHeight: 220, maxHeight: 720, zoomMin: 5000, zoomMax: 31 * 86400000,
        zoomKey: "ctrlKey", orientation: { axis: "top", item: "top" },
        groupOrder: "order", groupTemplate: groupLabel, groupHeightMode: "auto",
        verticalScroll: true, selectable: true, editable: false,
        showCurrentTime: false, showTooltips: true,
        moment: date => data.zone === "utc" ? vis.moment(date).utc() : vis.moment(date),
        format: { minorLabels: { second: "HH:mm:ss", minute: "HH:mm", hour: "HH:mm", day: "D MMM" }, majorLabels: { second: "YYYY-MM-DD", minute: "YYYY-MM-DD", hour: "YYYY-MM-DD", day: "MMMM YYYY" } }
    });
    timeline.addCustomTime(new Date(data.now), "now");
    timeline.setCustomTimeTitle("Current time (clock pauses offscreen and with reduced motion)", "now");
    const state = { timeline, items, groups, callback, visible: data.visible, intersecting: false, motion: matchMedia("(prefers-reduced-motion: reduce)"), destroyed: false, expanded: new Map() };
    timeline.on("rangechanged", event => {
        if (!event.byUser) return;
        state.following = false;
        clearTimeout(state.rangeTimer);
        state.rangeTimer = setTimeout(() => {
            if (!state.destroyed) state.callback.invokeMethodAsync("RangeChanged", event.start.toISOString(), event.end.toISOString()).catch(() => {});
        }, 350);
    });
    timeline.on("select", event => {
        if (!event.items.length) return;
        const item = items.get(event.items[0]);
        const url = item?.url && safeUrl(item.url);
        if (url) location.assign(url);
    });
    state.visibility = () => restartClock(state);
    document.addEventListener("visibilitychange", state.visibility);
    state.motion.addEventListener("change", state.visibility);
    state.observer = new IntersectionObserver(entries => { state.intersecting = entries.some(entry => entry.isIntersecting); restartClock(state); });
    state.observer.observe(element);
    timelines.set(element, state);
    return state;
}

export async function render(element, data, callback) {
    if (!data.visible) {
        const state = timelines.get(element);
        if (state) { state.visible = false; restartClock(state); }
        return;
    }
    const vis = await loadLibrary(data);
    if (!element.isConnected) return;
    const state = timelines.get(element) ?? createTimeline(element, data, vis, callback);
    const changedView = state.viewKey !== data.viewKey;
    state.viewKey = data.viewKey;
    state.captured = new Date(data.now).getTime();
    state.received = performance.now();
    state.following = data.follow;
    state.visible = data.visible;
    state.groups.get().forEach(group => { if (group.nestedGroups) state.expanded.set(group.id, group.showNested); });
    updateDataSet(state.groups, data.groups.map((group, order) => ({ ...group, order, subgroupStack: { runs: true, forecast: false }, ...(group.nestedGroups ? { showNested: state.expanded.get(group.id) ?? false } : {}) })));
    updateDataSet(state.items, data.items.map(itemData));
    if (state.zone !== data.zone) {
        state.zone = data.zone;
        state.timeline.setOptions({ moment: date => data.zone === "utc" ? vis.moment(date).utc() : vis.moment(date) });
    }
    state.timeline.setCustomTime(new Date(data.now), "now");
    if (changedView) state.timeline.setWindow(new Date(data.start), new Date(data.end), { animation: false });
    if (data.visible) state.timeline.redraw();
    restartClock(state);
}

export function localTimeZone() { return Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC"; }
export function prefersList() { return matchMedia("(max-width: 640px)").matches; }

export function destroy(element) {
    const state = timelines.get(element);
    if (!state) return;
    state.destroyed = true;
    clearInterval(state.clock);
    clearTimeout(state.rangeTimer);
    state.observer.disconnect();
    document.removeEventListener("visibilitychange", state.visibility);
    state.motion.removeEventListener("change", state.visibility);
    state.timeline.destroy();
    timelines.delete(element);
}
