// TgJelly dashboard page controller (spec §11.1). Loaded through data-controller="__plugin/tgjelly.js".
// Plan 1 shows the plugin status only; later plans add the setup UI.

const POLL_MS = 3000;

function render(view, status) {
    const compat = view.querySelector('#tgj-status-compat');
    compat.textContent = status.Compatible
        ? `TgJelly is running (build for Jellyfin ${status.BuildTarget}).`
        : status.CompatMessage;

    view.querySelector('#tgj-status-instance').textContent = status.InstanceMessage || '';
    view.querySelector('#tgj-status-version').textContent = `Version ${status.Version} · settings revision ${status.Revision}`;
}

export default function TgJellyPage(view) {
    let timer = null;

    const stop = () => {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    };

    const refresh = async () => {
        try {
            const status = await window.ApiClient.fetch({
                url: window.ApiClient.getUrl('TgJelly/Status'),
                type: 'GET',
                dataType: 'json',
                headers: { accept: 'application/json' }
            });
            render(view, status);
        } catch (err) {
            view.querySelector('#tgj-status-compat').textContent = 'Could not load the TgJelly status.';
            console.error('TgJelly status request failed', err);
        }
    };

    const start = () => {
        stop();
        refresh();
        timer = setInterval(() => {
            if (!view.isConnected || view.classList.contains('hide')) {
                stop();
                return;
            }
            if (!document.hidden) {
                refresh();
            }
        }, POLL_MS);
    };

    view.addEventListener('viewshow', start);
    view.addEventListener('viewbeforehide', stop);
    view.addEventListener('viewhide', stop);
    view.addEventListener('viewdestroy', stop);
}
