(function () {
    function urlBase64ToUint8Array(base64) {
        const padding = '='.repeat((4 - (base64.length % 4)) % 4);
        const raw = atob((base64 + padding).replace(/-/g, '+').replace(/_/g, '/'));
        return Uint8Array.from(raw, c => c.charCodeAt(0));
    }

    async function post(url, body) {
        const response = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        if (!response.ok) {
            throw new Error(url + ' returned ' + response.status);
        }
    }

    async function init() {
        const button = document.getElementById('push-toggle');
        const status = document.getElementById('push-status');
        if (!button || button.dataset.initialized) {
            return;
        }
        button.dataset.initialized = 'true';

        if (!('serviceWorker' in navigator) || !('PushManager' in window) || !('Notification' in window)) {
            status.textContent = 'Notifications are not supported in this browser.';
            return;
        }

        const keyResponse = await fetch('/api/push/public-key');
        if (!keyResponse.ok) {
            status.textContent = 'Notifications are not configured on the server.';
            return;
        }
        const publicKey = await keyResponse.text();

        const registration = await navigator.serviceWorker.register('/sw.js');
        let subscription = await registration.pushManager.getSubscription();

        function render() {
            button.hidden = Notification.permission === 'denied';
            button.textContent = subscription ? 'Disable notifications' : 'Enable notifications';
            status.textContent = Notification.permission === 'denied'
                ? 'Notifications are blocked in browser settings.'
                : (subscription ? 'Notifications on for this device.' : '');
        }

        // The server store is in-memory; re-register the existing subscription after a restart.
        if (subscription) {
            await post('/api/push/subscribe', subscription.toJSON()).catch(() => { });
        }
        render();

        button.addEventListener('click', async () => {
            button.disabled = true;
            try {
                if (subscription) {
                    await post('/api/push/unsubscribe', { endpoint: subscription.endpoint });
                    await subscription.unsubscribe();
                    subscription = null;
                } else {
                    const permission = await Notification.requestPermission();
                    if (permission === 'granted') {
                        subscription = await registration.pushManager.subscribe({
                            userVisibleOnly: true,
                            applicationServerKey: urlBase64ToUint8Array(publicKey)
                        });
                        await post('/api/push/subscribe', subscription.toJSON());
                    }
                }
            } catch (e) {
                status.textContent = 'Failed: ' + e.message;
            } finally {
                button.disabled = false;
                render();
            }
        });
    }

    document.addEventListener('DOMContentLoaded', init);
    if (document.readyState !== 'loading') {
        init();
    }
    if (window.Blazor) {
        window.Blazor.addEventListener('enhancedload', init);
    }
})();
