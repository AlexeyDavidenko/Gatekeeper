// Called from RichTextEditor.razor's own OnAfterRenderAsync — see the comment there for why this is
// driven by the component's render lifecycle rather than a document-level DOMContentLoaded listener.
window.gatekeeperRichText = {
    // Reads the hidden input's current value — the .rte-editor's own 'input' listener (below) keeps
    // it live-synced on every keystroke, so this just returns whatever's already there. Needed
    // because dialog-based submits (QuestionDialog.razor) call this explicitly instead of relying
    // on a native <form> POST to serialize the hidden input for them.
    getValue(wrapper) {
        return wrapper.querySelector('input[type=hidden]')?.value ?? '';
    },

    init(wrapper) {
        const editor = wrapper.querySelector('.rte-editor');
        const hidden = wrapper.querySelector('input[type=hidden]');
        if (!editor || !hidden) return;

        const sync = () => { hidden.value = editor.innerHTML; };

        editor.addEventListener('input', sync);

        // Question prompts are single-line — don't let Enter insert a line break.
        editor.addEventListener('keydown', e => {
            if (e.key === 'Enter') e.preventDefault();
        });

        // Paste as plain text only, so foreign styles/tags from elsewhere don't clutter the editor
        // (server-side sanitization is the real security boundary regardless — this is just UX).
        editor.addEventListener('paste', e => {
            e.preventDefault();
            const text = (e.clipboardData || window.clipboardData).getData('text/plain');
            document.execCommand('insertText', false, text);
        });

        wrapper.querySelectorAll('.rte-toolbar button').forEach(btn => {
            // mousedown (not click) so the editor never loses focus/selection before the command runs.
            btn.addEventListener('mousedown', e => e.preventDefault());
            btn.addEventListener('click', () => {
                editor.focus();
                const cmd = btn.dataset.cmd;
                if (cmd === 'link') {
                    const url = prompt('Ссылка (https://...)');
                    if (url) document.execCommand('createLink', false, url);
                } else if (cmd === 'code') {
                    const sel = window.getSelection();
                    if (sel && sel.rangeCount && !sel.isCollapsed) {
                        const range = sel.getRangeAt(0);
                        const code = document.createElement('code');
                        code.appendChild(range.extractContents());
                        range.insertNode(code);
                    }
                } else {
                    document.execCommand(cmd);
                }
                sync();
            });
        });

        // A hidden <input required> can't be validated by the browser (it can't be focused to show
        // the bubble) — so a required editor is enforced here instead, on the enclosing form.
        if (editor.dataset.required === 'true') {
            const form = wrapper.closest('form');
            form?.addEventListener('submit', e => {
                if (editor.textContent.trim() === '') {
                    e.preventDefault();
                    editor.focus();
                }
            });
        }
    },
};
