/* Modal de confirmação e toasts — sem dependências. */
(() => {
    "use strict";

    let toastHost = null;

    function host() {
        if (!toastHost) {
            toastHost = document.createElement("div");
            toastHost.className = "toasts";
            toastHost.setAttribute("aria-live", "polite");
            document.body.appendChild(toastHost);
        }
        return toastHost;
    }

    /** type: "info" | "ok" | "warn" | "error" */
    function toast(message, { type = "info", timeout = 7000 } = {}) {
        const item = document.createElement("div");
        item.className = `toast toast-${type}`;
        item.textContent = message;
        item.addEventListener("click", () => item.remove());
        host().appendChild(item);

        if (timeout > 0) setTimeout(() => item.remove(), timeout);
        return item;
    }

    /** Substitui window.confirm; devolve Promise<boolean>. */
    function confirmDialog({ title, message, confirmText = "Confirmar", cancelText = "Cancelar", danger = false }) {
        return new Promise(resolve => {
            const dialog = document.createElement("dialog");
            dialog.className = "modal";

            const body = document.createElement("div");
            body.className = "modal-body";
            const heading = document.createElement("h2");
            heading.textContent = title;
            const text = document.createElement("p");
            text.textContent = message;
            body.append(heading, text);

            const foot = document.createElement("div");
            foot.className = "modal-foot";
            const cancel = document.createElement("button");
            cancel.type = "button";
            cancel.className = "btn-ghost";
            cancel.textContent = cancelText;
            const ok = document.createElement("button");
            ok.type = "button";
            ok.className = danger ? "btn-danger" : "";
            ok.textContent = confirmText;
            foot.append(cancel, ok);

            dialog.append(body, foot);
            document.body.appendChild(dialog);

            const close = result => {
                dialog.close();
                dialog.remove();
                resolve(result);
            };

            cancel.addEventListener("click", () => close(false));
            ok.addEventListener("click", () => close(true));
            // Esc fecha nativamente.
            dialog.addEventListener("cancel", e => { e.preventDefault(); close(false); });

            dialog.showModal();
            ok.focus();
        });
    }

    window.ui = { toast, confirm: confirmDialog };
})();
