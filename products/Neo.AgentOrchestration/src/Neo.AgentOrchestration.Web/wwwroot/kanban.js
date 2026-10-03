/* Pointer and keyboard enhancement; the ordinary form remains authoritative. */
(() => {
    'use strict';
    let drag = null, suppressClickUntil = 0;
    const board = () => document.querySelector('.kanban');
    const announce = text => { const node = document.getElementById('kanban-announcement'); if (node) node.textContent = text; };
    const destinations = card => [...card.querySelectorAll('[name="Destination"] option')].map(x => x.value).filter(Boolean);
    function updateClaim(form) {
        const claim = form.querySelector('.claim-fields');
        if (!claim) return;
        const needed = form.elements.Destination.value === 'InProgress';
        claim.hidden = !needed;
        form.elements.ClaimRoleId.required = needed;
    }
    function openMove(card, state) {
        if (window.NeoWorkbench?.busy || !card?.isConnected) return;
        const menu = card.querySelector('.move-menu'), form = card.querySelector('.move-form');
        if (!menu || !form) return;
        if (state && !destinations(card).includes(state)) { announce('این مرحله برای کارت انتخاب‌شده مجاز نیست.'); return; }
        menu.open = true;
        if (state) {
            form.elements.Destination.value = state;
            form.elements.Destination.dispatchEvent(new Event('change', { bubbles: true }));
        }
        updateClaim(form);
        card.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        form.elements.Destination.focus({ preventScroll: true });
        announce('مقصد را بررسی کنید و «تأیید انتقال» را بزنید. کارت هنوز جابه‌جا نشده است.');
    }
    function cleanup() {
        const previous = drag;
        drag = null;
        if (previous) {
            cancelAnimationFrame(previous.frame);
            if (previous.handle.hasPointerCapture(previous.id)) previous.handle.releasePointerCapture(previous.id);
            previous.card.classList.remove('moving-card');
        }
        document.querySelectorAll('.drop-allowed,.drop-denied,.drop-target').forEach(x => x.classList.remove('drop-allowed','drop-denied','drop-target'));
    }
    function highlight(x, y) {
        document.querySelectorAll('.drop-target').forEach(node => node.classList.remove('drop-target'));
        const target = document.elementFromPoint(x, y)?.closest('.column[data-state]');
        if (target?.classList.contains('drop-allowed')) target.classList.add('drop-target');
        return target;
    }
    function scrollDrag() {
        if (!drag) return;
        if (drag.moved) {
            const region = board(), rect = region?.getBoundingClientRect();
            if (rect && drag.y >= rect.top && drag.y <= rect.bottom) {
                const left = Math.max(0, rect.left), right = Math.min(innerWidth, rect.right);
                if (drag.x < left + 45) region.scrollBy(-12, 0);
                else if (drag.x > right - 45) region.scrollBy(12, 0);
                highlight(drag.x, drag.y);
            }
        }
        drag.frame = requestAnimationFrame(scrollDrag);
    }
    document.addEventListener('pointerdown', event => {
        const handle = event.target.closest?.('.move-handle');
        if (!handle || event.button !== 0 || !event.isPrimary || window.NeoWorkbench?.busy || drag) return;
        const card = handle.closest('.card');
        drag = { handle, card, id: event.pointerId, startX: event.clientX, startY: event.clientY, x: event.clientX, y: event.clientY, moved: false, frame: 0 };
        handle.setPointerCapture(event.pointerId);
    });
    document.addEventListener('pointermove', event => {
        if (!drag || drag.id !== event.pointerId) return;
        drag.x = event.clientX; drag.y = event.clientY;
        if (!drag.moved && Math.hypot(drag.x - drag.startX, drag.y - drag.startY) < 8) return;
        if (!drag.moved) {
            drag.moved = true;
            drag.card.classList.add('moving-card');
            const allowed = destinations(drag.card);
            board()?.querySelectorAll('.column').forEach(x => x.classList.add(allowed.includes(x.dataset.state) ? 'drop-allowed' : 'drop-denied'));
            announce('فقط ستون‌های مشخص‌شده مقصد مجاز هستند؛ Escape برای انصراف.');
            drag.frame = requestAnimationFrame(scrollDrag);
        }
        event.preventDefault();
        highlight(drag.x, drag.y);
    }, { passive: false });
    document.addEventListener('pointerup', event => {
        if (!drag || drag.id !== event.pointerId) return;
        const current = drag, target = highlight(event.clientX, event.clientY);
        cleanup();
        if (current.moved) {
            suppressClickUntil = Date.now() + 400;
            if (target && destinations(current.card).includes(target.dataset.state)) openMove(current.card, target.dataset.state);
            else announce('انتقال انجام نشد؛ یک ستون مجاز را انتخاب کنید.');
        }
    });
    document.addEventListener('pointercancel', cleanup);
    document.addEventListener('lostpointercapture', () => { if (drag) cleanup(); });
    document.addEventListener('keydown', event => { if (event.key === 'Escape' && drag) { cleanup(); announce('جابه‌جایی لغو شد.'); } });
    document.addEventListener('click', event => {
        const handle = event.target.closest?.('.move-handle');
        if (handle && Date.now() >= suppressClickUntil) openMove(handle.closest('.card'));
    });
    document.addEventListener('change', event => { if (event.target.matches?.('.move-form [name="Destination"]')) updateClaim(event.target.form); });
    document.addEventListener('neo:board-updated', event => {
        const before = event.detail?.before;
        if (!(before instanceof Map)) return;
        document.querySelectorAll('.kanban-card-compact[data-item-id]').forEach(card => {
            const previous = before.get(card.dataset.itemId);
            if (!previous || !previous.agent || previous.state === card.dataset.state || card.dataset.agentOwned !== 'true') return;
            card.classList.remove('agent-move-arrival');
            void card.offsetWidth;
            card.classList.add('agent-move-arrival');
            const announcement = document.getElementById('kanban-announcement');
            if (announcement) announcement.textContent = `ایجنت کارت ${card.querySelector('[dir="ltr"]')?.textContent?.trim() || ''} را به مرحله جدید برد.`;
            setTimeout(() => card.classList.remove('agent-move-arrival'), 1100);
        });
    });
    function enhance() {
        cleanup();
        document.querySelectorAll('.move-handle').forEach(x => x.hidden = false);
        document.querySelectorAll('.move-form').forEach(updateClaim);
    }
    document.addEventListener('neo:navigated', enhance);
    enhance();
})();
