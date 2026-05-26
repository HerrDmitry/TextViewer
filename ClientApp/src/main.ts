// Application entry point — renders Hello World into <app-root>
document.addEventListener('DOMContentLoaded', () => {
    const root = document.querySelector('app-root');
    if (root) {
        root.innerHTML = '<h1>Hello World</h1>';
    }
});
