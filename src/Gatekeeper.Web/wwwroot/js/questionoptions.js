document.addEventListener('DOMContentLoaded', () => {
    document.querySelectorAll('.q-type-select').forEach(select => {
        const optionsBlock = select.closest('form')?.querySelector('.q-options');
        if (!optionsBlock) return;
        const sync = () => { optionsBlock.hidden = select.value !== 'SingleChoice' && select.value !== 'MultiChoice'; };
        select.addEventListener('change', sync);
        sync();
    });
});

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
