(function () {
    'use strict';
    function createLoader(options) {
        let generation = 0, current;
        const request = options.fetch || globalThis.fetch;
        const schedule = options.setTimeout || globalThis.setTimeout;
        const cancel = options.clearTimeout || globalThis.clearTimeout;
        const maximumBytes = 4 * 1024 * 1024;
        function abortable(operation, signal) {
            return new Promise((resolve, reject) => {
                const aborted = () => reject(new Error('aborted'));
                const cleanup = () => signal.removeEventListener('abort', aborted);
                Promise.resolve(operation).then(value => {
                    cleanup();
                    if (signal.aborted) aborted(); else resolve(value);
                }, error => { cleanup(); reject(error); });
                if (signal.aborted) aborted();
                else signal.addEventListener('abort', aborted, { once: true });
            });
        }
        async function read(response, signal) {
            if (Number(response.headers.get('content-length')) > maximumBytes) throw new Error('oversized');
            if (!response.body?.getReader) {
                const text = await abortable(response.text(), signal);
                if (new TextEncoder().encode(text).length > maximumBytes) throw new Error('oversized');
                return text;
            }
            const reader = response.body.getReader(), parts = [];
            let size = 0;
            try {
                while (true) {
                    const part = await abortable(reader.read(), signal);
                    if (part.done) break;
                    size += part.value.byteLength;
                    if (size > maximumBytes) throw new Error('oversized');
                    parts.push(part.value);
                }
            } catch (error) {
                try { Promise.resolve(reader.cancel()).catch(() => {}); } catch {}
                throw error;
            } finally { try { reader.releaseLock(); } catch {} }
            const bytes = new Uint8Array(size);
            let offset = 0;
            for (const part of parts) { bytes.set(part, offset); offset += part.byteLength; }
            return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
        }
        return {
            async load(organizationId) {
                const id = ++generation;
                current?.abort();
                const controller = new AbortController(); current = controller;
                let timedOut = false;
                const timer = schedule(() => { timedOut = true; controller.abort(); }, 50000);
                try {
                    options.onStart();
                    const url = new URL(options.url);
                    url.searchParams.set('handler', 'Snapshot');
                    url.searchParams.delete('tenantId');
                    if (organizationId) url.searchParams.set('tenantId', organizationId);
                    url.hash = '';
                    const response = await abortable(request(url, { cache: 'no-store', credentials: 'same-origin', redirect: 'error', signal: controller.signal }), controller.signal);
                    if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) throw new Error('workspace');
                    const text = await read(response, controller.signal);
                    if (id !== generation) return;
                    if (controller.signal.aborted) throw new Error('aborted');
                    const data = options.parse(text);
                    options.validate(data);
                    options.onData(data);
                } catch {
                    if (id === generation) options.onError(timedOut);
                } finally {
                    cancel(timer);
                    if (id === generation) options.onEnd();
                }
            }
        };
    }
    const api = { createLoader };
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    else globalThis.FanasaWorkspaceTransport = api;
})();
