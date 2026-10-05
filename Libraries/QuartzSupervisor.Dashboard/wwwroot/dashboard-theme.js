(() => {
    const key = "QuartzSupervisor.theme";
    const media = window.matchMedia("(prefers-color-scheme: dark)");

    function savedTheme() {
        try {
            const value = localStorage.getItem(key);
            return value === "light" || value === "dark" ? value : null;
        } catch {
            return null;
        }
    }

    function currentTheme() {
        return savedTheme() ?? (media.matches ? "dark" : "light");
    }

    function apply(theme) {
        document.documentElement.dataset.theme = theme;
        document.documentElement.style.colorScheme = theme;
    }

    apply(currentTheme());
    media.addEventListener("change", () => {
        if (savedTheme() === null)
            apply(currentTheme());
    });

    window.quartzSupervisorTheme = {
        isDark: () => currentTheme() === "dark",
        toggle: () => {
            const theme = currentTheme() === "dark" ? "light" : "dark";
            try { localStorage.setItem(key, theme); }
            catch { }
            apply(theme);
            return theme === "dark";
        }
    };
    const followingTimelines = new WeakSet();
    function trackTimeline(element) {
        if (element.dataset.followTracking)
            return;
        element.dataset.followTracking = "true";
        for (const event of ["wheel", "touchstart", "pointerdown", "keydown"])
            element.addEventListener(event, () => followingTimelines.delete(element), { passive: true });
    }

    window.quartzSupervisorTimeline = {
        isAtEnd: element => {
            trackTimeline(element);
            return followingTimelines.has(element) ||
                element.scrollWidth - element.scrollLeft - element.clientWidth < 24;
        },
        scrollToLatest: element => {
            trackTimeline(element);
            followingTimelines.add(element);
            element.scrollTo({ left: element.scrollWidth });
        }
    };
})();
