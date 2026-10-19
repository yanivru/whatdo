self.addEventListener('push', event => {
    let data = {};
    try {
        data = event.data ? event.data.json() : {};
    } catch {
        data = {};
    }

    const title = data.title || 'Copilot needs input';
    event.waitUntil(self.registration.showNotification(title, {
        body: data.body || '',
        tag: data.tag,
        icon: '/favicon.png',
        renotify: true
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        for (const client of windows) {
            if ('focus' in client) {
                return client.focus();
            }
        }
        return self.clients.openWindow('/');
    })());
});
