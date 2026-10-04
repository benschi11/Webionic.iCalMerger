// Merkt sich das Element, das vor dem Öffnen eines Dialogs den Fokus hatte, und gibt ihn beim Schließen zurück.
// ES-Modul, wird von ConfirmDialog.razor per JS-Isolation geladen.
let previous = null;

export function capture() {
    previous = document.activeElement;
}

export function restore() {
    if (previous && previous.isConnected) previous.focus();
    previous = null;
}
