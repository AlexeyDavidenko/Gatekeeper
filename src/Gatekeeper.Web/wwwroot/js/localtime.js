// Static SSR only (no Blazor client runtime here) — every navigation is a real page load, so a plain
// DOMContentLoaded listener is enough; timestamps render server-side in UTC and get rewritten here in
// the browser's own timezone.
document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('.local-time[data-utc]').forEach(el => {
        const d = new Date(el.dataset.utc);
        if (!isNaN(d.getTime())) {
            el.textContent = d.toLocaleString(undefined, {
                year: 'numeric', month: '2-digit', day: '2-digit',
                hour: '2-digit', minute: '2-digit',
            });
        }
    });
});
