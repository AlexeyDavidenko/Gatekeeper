// Called from LocalTime.razor's own OnAfterRenderAsync — see the comment there for why this is
// driven by the component's render lifecycle rather than a document-level DOMContentLoaded listener.
window.gatekeeperLocalTime = {
    format(el, utcIso) {
        const d = new Date(utcIso);
        if (!isNaN(d.getTime())) {
            el.textContent = d.toLocaleString(undefined, {
                year: 'numeric', month: '2-digit', day: '2-digit',
                hour: '2-digit', minute: '2-digit',
            });
        }
    },
};
