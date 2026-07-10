// select-toggle wiring is called from QuestionTypeEditor.razor's own OnAfterRenderAsync — see the
// comment there for why this is driven by the component's render lifecycle rather than a
// document-level DOMContentLoaded listener.
window.gatekeeperQuestionOptions = {
    init(select) {
        const optionsBlock = select.closest('form')?.querySelector('.q-options');
        if (!optionsBlock) return;
        const sync = () => { optionsBlock.hidden = select.value !== 'SingleChoice' && select.value !== 'MultiChoice'; };
        select.addEventListener('change', sync);
        sync();
    },
};

// Add/remove option rows: delegated on `document` itself, attached once, forever — unlike the
// per-instance wiring above, this never needs re-attaching, since it isn't tied to any specific
// rendered node that Blazor might later replace.
document.addEventListener('click', (e) => {
    const addBtn = e.target.closest('.q-add-option');
    if (addBtn) {
        const list = addBtn.closest('.q-options').querySelector('.q-options-list');
        const row = document.createElement('div');
        row.className = 'q-option-row';
        row.innerHTML = '<input type="text" name="options" /><button type="button" class="link q-remove-option">✕</button>';
        list.appendChild(row);
        return;
    }
    const removeBtn = e.target.closest('.q-remove-option');
    if (removeBtn) removeBtn.closest('.q-option-row').remove();
});
