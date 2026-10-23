export function prepare(input) {
    if (!input?.isConnected) return;
    input.addEventListener('keydown', event => {
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) event.preventDefault();
    });
    input.focus({ preventScroll: true });
}

export function scrollActive(input) {
    if (!input?.isConnected) return;
    const active = input.ownerDocument.getElementById(input.getAttribute('aria-activedescendant'));
    active?.scrollIntoView({ block: 'nearest' });
}
