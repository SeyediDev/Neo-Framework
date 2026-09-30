# Fanasa visual identity

The public introduction lives at `/product`, reachable from the shared header.
It uses `product.css`, static illustrative data, and no workspace/API access.
The existing `/` entry still selects the configured board. Operational pages
keep their authentication rules. Roadmap features are explicitly labeled, not
marketed as production-ready SaaS. No tracking scripts or external assets load.

The shared workspace navigation displays the user-approved English signature
"Seeking The Best For The Best" with LTR typography inside the RTL layout.
This replaces the visible imported workspace label only; stored workspace names,
identifiers and navigation routes are not renamed. Decorative accents belong to
the signature, never to the original logo.

The navigation marks the current page with `aria-current`; creating work is a
distinct action. Filters wrap with wider desktop fields and two mobile columns.
The horizontally scrollable board is keyboard-focusable with a text hint.
Card, metric, table and pagination treatments share the same visual hierarchy.
No client-side scripts, task-state changes or new drag-and-drop behavior are added.

The user requested the Fanasa identity for Neo Agent Orchestration Web.
Neo remains the product name; this is not a rename to a capability center.

Source: the Fanasa brand guide captured in the Basalam project's
`Fanasa/_work/fanasa_brand.html`, referenced by the conversation
«تهیه پروپوزال و قرارداد» (01a0e1fa-8caf-77c0-b132-bdf609862dfb).
The horizontal Persian color SVG is extracted unchanged from the preview
immediately preceding `/brand-assets/fanasa-logo-horizontal-fa-color.svg`.
It is a brand asset, not newly generated artwork. Brand rights remain with Fanasa.
No contract text, customer information or local configuration is included.

Palette: lapis #1F4B9A, turquoise #21B5A8, turquoise on light #149A8E,
saffron #E0A334, plaster #F6F1E7, ink #141C33.
`wwwroot/fanasa.css` is the shared presentation layer for all Razor pages.
Saffron/turquoise are accents, not low-contrast body text. Links/actions use
lapis. Error states and dynamic per-flow colors retain their semantic meaning.

Preserve logo proportions, original colors, and a clear zone of one quarter
of its height. Never mirror, recolor, add shadows/gradients, or stretch the logo.
Horizontal lockups must remain at least 24px high (Web uses 40px/32px).
Keep the user's self-hosted Vazir font and RTL throughout, including controls.

Future PWA clients should reuse these palette and asset rules. The theme-color
metadata does not imply a manifest, offline support or installable PWA exists.
This presentation-only change does not alter API/MCP/skill tool contracts.
