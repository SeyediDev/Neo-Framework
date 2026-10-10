/*
 * Static-shell PWA worker. Never cache HTML, API responses, cookies, or any
 * tenant/task content. A user must be online for authenticated navigation.
 */
const VERSION = 'work-management-static-v2';
const STATIC_ASSETS = [
  '/site.css',
  '/fanasa.css',
  '/workbench.js',
  '/kanban.js',
  '/manifest.webmanifest',
  '/brand/fanasa-horizontal-fa.svg'
];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(VERSION).then(cache => cache.addAll(STATIC_ASSETS)));
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys().then(keys => Promise.all(
    keys.filter(key => key !== VERSION).map(key => caches.delete(key))
  )));
  self.clients.claim();
});

self.addEventListener('fetch', event => {
  const request = event.request;
  if (request.method !== 'GET' || new URL(request.url).origin !== self.location.origin) return;
  const destination = request.destination;
  if (!['style', 'script', 'image', 'manifest'].includes(destination)) return;
  event.respondWith(caches.match(request).then(cached => cached || fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(VERSION).then(cache => cache.put(request, copy));
    }
    return response;
  })));
});
