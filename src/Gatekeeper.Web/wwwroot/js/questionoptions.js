// select-toggle wiring is called from QuestionTypeEditor.razor's own OnAfterRenderAsync — see the
// comment there for why this is driven by the component's render lifecycle rather than a
// document-level DOMContentLoaded listener. Looks up the select/options-block from the component's
// own container (not `closest('form')`) since QuestionDialog.razor hosts this with no <form>
// ancestor at all — submission is a dialog button click, not a native form POST.
window.gatekeeperQuestionOptions = {
    init(container) {
        const select = container.querySelector('.q-type-select');
        const optionsBlock = container.querySelector('.q-options');
        if (!select || !optionsBlock) return;
        const sync = () => { optionsBlock.hidden = select.value !== 'SingleChoice' && select.value !== 'MultiChoice'; };
        select.addEventListener('change', sync);
        sync();
    },

    // Reads the selected type plus the current RU/EN option values straight from the DOM, in row
    // order — the source of truth for the select and these dynamically added/removed rows lives
    // only there, never in Blazor's own state. Used by QuestionDialog.razor right before saving.
    collect(container) {
        const rows = [...container.querySelectorAll('.q-option-row')];
        return {
            type: container.querySelector('.q-type-select')?.value ?? 'Text',
            options: rows.map(r => r.querySelector('input[name=options]')?.value ?? ''),
            optionsEn: rows.map(r => r.querySelector('input[name=optionsEn]')?.value ?? ''),
        };
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
        row.innerHTML = '<input type="text" name="options" /><input type="text" name="optionsEn" placeholder="EN" /><button type="button" class="link q-remove-option">✕</button>';
        list.appendChild(row);
        return;
    }
    const removeBtn = e.target.closest('.q-remove-option');
    if (removeBtn) removeBtn.closest('.q-option-row').remove();
});
