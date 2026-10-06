const timelines = new WeakMap();
const windowMilliseconds = 10 * 60 * 1000;
const minimumWindowMilliseconds = 5 * 1000;
let libraryPromise;
let stylesheetPromise;

function loadStylesheet() {
    stylesheetPromise ??= new Promise((resolve, reject) => {
        const link = document.createElement("link");
        link.rel = "stylesheet";
        link.href = "/_content/QuartzSupervisor/vendor/vis-timeline/styles/vis-timeline-graph2d.min.css";
        link.onload = resolve;
        link.onerror = () => {
            stylesheetPromise = undefined;
            reject(new Error("The timeline stylesheet could not be loaded."));
        };
        document.head.append(link);
    });
    return stylesheetPromise;
}

function loadLibrary() {
    const styles = loadStylesheet();
    if (window.vis?.Timeline)
        return styles.then(() => window.vis);

    libraryPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = "/_content/QuartzSupervisor/vendor/vis-timeline/standalone/umd/vis-timeline-graph2d.min.js";
        script.onload = () => resolve(window.vis);
        script.onerror = () => {
            libraryPromise = undefined;
            reject(new Error("The timeline library could not be loaded."));
        };
        document.head.append(script);
    });
    return Promise.all([libraryPromise, styles]).then(([vis]) => vis);
}

function groupLabel(group) {
    const label = document.createElement("div");
    label.className = "quartz-timeline-group";
    const name = document.createElement("strong");
    name.textContent = group.name;
    const details = document.createElement("span");
    details.textContent = `${group.jobGroup} · ${group.schedulerName}`;
    label.append(name, details);
    return label;
}

function updateDataSet(dataSet, values) {
    const ids = new Set(values.map(value => value.id));
    const removed = dataSet.getIds().filter(id => !ids.has(id));
    if (removed.length)
        dataSet.remove(removed);
    if (values.length)
        dataSet.update(values);
}

function createTimeline(element, data, vis) {
    const now = new Date(data.now);
    const items = new vis.DataSet(data.items);
    const groups = new vis.DataSet(data.groups.map((group, order) => ({ ...group, order })));
    const timeline = new vis.Timeline(element, items, groups, {
        start: new Date(now.getTime() - windowMilliseconds),
        end: now,
        minHeight: 220,
        maxHeight: 720,
        zoomMin: minimumWindowMilliseconds,
        zoomMax: windowMilliseconds,
        zoomKey: "ctrlKey",
        orientation: { axis: "top", item: "top" },
        groupOrder: "order",
        groupTemplate: groupLabel,
        groupHeightMode: "fitItems",
        verticalScroll: true,
        selectable: true,
        editable: false,
        showCurrentTime: true,
        showTooltips: true,
        moment: date => vis.moment(date).utc(),
        format: {
            minorLabels: { second: "HH:mm:ss", minute: "HH:mm", hour: "HH:mm", day: "D MMM" },
            majorLabels: { second: "YYYY-MM-DD", minute: "YYYY-MM-DD", hour: "YYYY-MM-DD", day: "MMMM YYYY" }
        }
    });

    const state = { timeline, items, groups, now, windowMilliseconds, following: true };
    timeline.on("rangechanged", () => {
        const range = timeline.getWindow();
        state.windowMilliseconds = Math.min(windowMilliseconds, range.end - range.start);
        state.following = range.end.getTime() >= state.now.getTime() - Math.max(2000, state.windowMilliseconds * 0.02);
    });
    timelines.set(element, state);
    timeline.setCurrentTime(now);
    return state;
}

export async function render(element, data) {
    const vis = await loadLibrary();
    let state = timelines.get(element);
    if (!state)
        state = createTimeline(element, data, vis);

    state.now = new Date(data.now);
    updateDataSet(state.items, data.items);
    updateDataSet(state.groups, data.groups.map((group, order) => ({ ...group, order })));
    state.timeline.setCurrentTime(state.now);

    if (state.following) {
        state.timeline.setWindow(
            new Date(state.now.getTime() - state.windowMilliseconds),
            state.now,
            { animation: false });
    } else {
        state.timeline.redraw();
    }
}

export function zoom(element, factor) {
    const state = timelines.get(element);
    if (!state)
        return;

    const range = state.timeline.getWindow();
    const span = Math.max(minimumWindowMilliseconds, Math.min(windowMilliseconds, (range.end - range.start) * factor));
    const center = (range.start.getTime() + range.end.getTime()) / 2;
    state.windowMilliseconds = span;
    state.timeline.setWindow(new Date(center - span / 2), new Date(center + span / 2));
}

export function goLive(element, now) {
    const state = timelines.get(element);
    if (!state)
        return;

    state.now = new Date(now);
    state.windowMilliseconds = windowMilliseconds;
    state.following = true;
    state.timeline.setWindow(new Date(state.now.getTime() - windowMilliseconds), state.now);
}

export function destroy(element) {
    const state = timelines.get(element);
    if (!state)
        return;

    state.timeline.destroy();
    timelines.delete(element);
}
