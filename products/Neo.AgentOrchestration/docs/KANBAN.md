# Kanban transitions

The board offers a native per-card transition form (also works without scripts).
With `kanban.js`, its handle supports mouse/pen/touch Pointer Events. Drag across
columns to select a destination; allowed columns are outlined, others dimmed.
The board scrolls horizontally near the pointer edges. Escape or pointer cancel
abandons the gesture. Clicking/keyboard-activating the handle or opening the
native details menu provides the same operation without dragging.

A drop opens the card's confirmation form; it does **not** mutate a task. Explicit
confirmation submits the ordinary antiforgery-protected BFF handler through the
SPA layer. The card/counts change only after the API accepts and the filtered
board is refetched. A card can disappear from the current filter/page after a move.
No optimistic DOM relocation, automatic retry, within-column ordering or role
reassignment is performed. Native details/select/button controls support keyboard
and touch use. Server errors retain the card and inputs; stale versions require
explicit refresh and review before another attempt. Lost responses are uncertain.

`InProgress` uses API `claim`, requires an enabled free role and starts its timer.
Other destinations use API `status`. Server ownership, chat, scope, grants, record
version, dependencies and children still apply; UI destination hints are not
authorization. Ready clears assignment; Done/Cancelled are terminal. Archived and
terminal cards have no move control; managed-run ownership cannot be taken over.

Tests: KanbanMoveTests checks hints and actual Razor/API/isolated SQL transitions,
claim/timer semantics, stale versions, unfinished-child gates, CSRF and read-only
grants. Client SPA unit tests cover serialization/dedup/error handling. Browser
pointer, keyboard and touch acceptance must be recorded separately, not inferred
from HTTP test success. No external harness or public deployment is enabled.

## Verification recorded 2026-10-02

- Focused KanbanMove/WebManagement/SpaHost/LocalDevelopmentAccess suite: 37 passed.
- Client workbench + Kanban unit suite: 14 passed (fake DOM, including touch/pen).
- Local in-app browser: real pointer Backlog -> Ready with explicit confirmation;
  keyboard menu activation; missing-role validation; busy-role rejection; stale
  version rejection with draft preserved after refresh; Ready -> Blocked success.
  Column counters updated and the SPA document identity stayed unchanged.
- Synthetic record QA-KANBAN-20261002 is archived at Blocked, with history retained.
  Physical touch/pen hardware and external harness execution were not validated.
