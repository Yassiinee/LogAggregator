// Terminal viewport helpers. Loaded as a JS module by LogTerminal.razor, so nothing is
// added to the global namespace and the browser caches it independently of the app bundle.

const NEAR_BOTTOM_THRESHOLD_PX = 32;

export function scrollToBottom(element) {
    if (element) {
        element.scrollTop = element.scrollHeight;
    }
}

export function isNearBottom(element) {
    if (!element) {
        return true;
    }

    return element.scrollHeight - element.scrollTop - element.clientHeight <= NEAR_BOTTOM_THRESHOLD_PX;
}

// Lets the component drop out of follow-tail mode the moment the user scrolls up to read
// something, and resume when they scroll back down — the behaviour people expect from `less +F`.
export function attachScrollWatcher(element, dotNetRef) {
    if (!element || element._logScrollWatcher) {
        return;
    }

    let queued = false;
    let lastReported = null;

    const handler = () => {
        if (queued) {
            return;
        }

        queued = true;

        // Coalesce a burst of scroll events into one interop call per frame.
        requestAnimationFrame(() => {
            queued = false;
            const nearBottom = isNearBottom(element);

            if (nearBottom !== lastReported) {
                lastReported = nearBottom;
                dotNetRef.invokeMethodAsync('OnScrollPositionChanged', nearBottom);
            }
        });
    };

    element.addEventListener('scroll', handler, { passive: true });
    element._logScrollWatcher = handler;
}

export function detachScrollWatcher(element) {
    if (element?._logScrollWatcher) {
        element.removeEventListener('scroll', element._logScrollWatcher);
        delete element._logScrollWatcher;
    }
}
