const reconnectModal = document.getElementById("components-reconnect-modal");
const retryButton = document.getElementById("components-reconnect-button");
const resumeButton = document.getElementById("components-resume-button");
const refreshButton = document.getElementById("components-refresh-button");

reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);
retryButton.addEventListener("click", retry);
resumeButton.addEventListener("click", resume);
refreshButton.addEventListener("click", () => location.reload());

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        location.reload();
    }
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        const connected = await Blazor.reconnect();
        if (!connected && !await Blazor.resumeCircuit())
            location.reload();
        else if (!connected)
            reconnectModal.close();
    } catch {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        if (!await Blazor.resumeCircuit())
            location.reload();
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible")
        await retry();
}
