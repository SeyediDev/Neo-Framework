(function (root) {
    'use strict';
    function elapsed(base, tracking, started, now) {
        return Math.max(0, base + (tracking ? Math.floor(Math.max(0, now - started) / 1000) : 0));
    }
    function budget(seconds, estimate) { return estimate > 0 ? Math.round(1000 * seconds / estimate) / 10 : null; }
    function format(seconds) {
        return `${Math.floor(seconds / 3600).toLocaleString('fa-IR')} ساعت و ${String(Math.floor(seconds % 3600 / 60)).padStart(2, '0')} دقیقه و ${String(seconds % 60).padStart(2, '0')} ثانیه`;
    }
    if (typeof module !== 'undefined' && module.exports) { module.exports = { elapsed, budget, format }; return; }
    const anchors = new WeakMap();
    function tick() {
        const now = performance.now();
        for (const meter of document.querySelectorAll('[data-time-meter]')) {
            if (!anchors.has(meter)) anchors.set(meter, now);
            if (document.hidden) continue;
            const seconds = elapsed(Number(meter.dataset.elapsed), meter.dataset.tracking === 'true', anchors.get(meter), now);
            const label = meter.querySelector('[data-elapsed-label]');
            if (label) label.textContent = format(seconds);
            const percent = budget(seconds, Number(meter.dataset.estimate));
            if (percent !== null) {
                const progress = meter.querySelector('[data-time-progress]');
                if (progress) progress.value = Math.min(100, percent);
                const text = meter.querySelector('[data-time-budget]');
                if (text) text.textContent = `${percent.toLocaleString('fa-IR')}٪ از بودجه زمان`;
            }
        }
    }
    document.addEventListener('neo:navigated', tick);
    document.addEventListener('visibilitychange', tick);
    tick(); setInterval(tick, 1000);
})(typeof window !== 'undefined' ? window : this);
