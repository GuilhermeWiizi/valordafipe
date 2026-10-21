export function prepare(input) {
    input.addEventListener('keydown', event => {
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) event.preventDefault();
    });
}

export function scrollActive(input) {
    const active = document.getElementById(input.getAttribute('aria-activedescendant'));
    active?.scrollIntoView({ block: 'nearest' });
}
