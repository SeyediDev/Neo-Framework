"""Render the public Fanasa directory from an approved, nonsecret snapshot."""

from __future__ import annotations

import argparse
from datetime import datetime
from html import escape
import json
from pathlib import Path
import re
import shutil
from string import Template
from urllib.parse import urlsplit


ROOT = Path(__file__).resolve().parent
KEY = re.compile(r"[a-z][a-z0-9-]{0,63}")
STATUSES = {"ready": "", "setup": "در حال تکمیل", "unavailable": "فعلاً در دسترس نیست"}
ASSETS = ("fanasa-logo.svg", "Vazir-Regular.woff2", "Vazir-Bold.woff2")
PERSIAN_DIGITS = str.maketrans("0123456789", "۰۱۲۳۴۵۶۷۸۹")


def text(value: object, field: str, *, optional: bool = False) -> str:
    if optional and value is None:
        return ""
    if not isinstance(value, str) or (not optional and not value.strip()):
        raise ValueError(f"{field} must be a nonempty string")
    if len(value) > 3000:
        raise ValueError(f"{field} is too long")
    return escape(value.strip(), quote=True)


def public_url(value: object) -> tuple[str, str]:
    if not isinstance(value, str) or any(ord(char) <= 32 for char in value):
        raise ValueError("Application URL must be a plain HTTPS URL")
    parsed = urlsplit(value)
    host = parsed.hostname or ""
    if (
        parsed.scheme != "https"
        or not (host == "app.fanasacloud.ir" or host.endswith(".app.fanasacloud.ir"))
        or parsed.username is not None
        or parsed.password is not None
        or parsed.port not in (None, 443)
        or parsed.query
        or parsed.fragment
        or "\\" in value
    ):
        raise ValueError("Only public Fanasa HTTPS entry URLs without credentials or query data are allowed")
    return escape(value, quote=True), escape(host + parsed.path.rstrip("/"), quote=True)


def unique_key(value: object, used: set[str], field: str) -> str:
    if not isinstance(value, str) or not KEY.fullmatch(value) or value in used:
        raise ValueError(f"{field} must be a unique lowercase slug")
    used.add(value)
    return value


def render(catalog: dict) -> tuple[str, int]:
    if not isinstance(catalog, dict):
        raise ValueError("Catalog must be an object")
    groups = catalog.get("groups")
    if not isinstance(groups, list) or not groups:
        raise ValueError("Catalog must have at least one group")
    # Validate provenance time without exposing the input snapshot itself in the output.
    generated_at = catalog.get("generatedAt")
    if not isinstance(generated_at, str):
        raise ValueError("generatedAt must be an ISO timestamp")
    datetime.fromisoformat(generated_at.replace("Z", "+00:00"))
    group_keys: set[str] = set()
    app_keys: set[str] = set()
    sections: list[str] = []
    count = 0
    for number, group in enumerate(groups, 1):
        if not isinstance(group, dict):
            raise ValueError("Every group must be an object")
        key = unique_key(group.get("key"), group_keys, "group.key")
        title = text(group.get("title"), "group.title")
        description = text(group.get("description"), "group.description", optional=True)
        apps = group.get("apps")
        if not isinstance(apps, list) or not apps:
            raise ValueError("Every group must have at least one application")
        cards: list[str] = []
        for app in apps:
            if not isinstance(app, dict):
                raise ValueError("Every application must be an object")
            app_key = unique_key(app.get("key"), app_keys, "app.key")
            name = text(app.get("name"), "app.name")
            subtitle = text(app.get("subtitle"), "app.subtitle", optional=True)
            summary = text(app.get("description"), "app.description")
            note = text(app.get("note"), "app.note", optional=True)
            link_label = text(app.get("linkLabel", "باز کردن سامانه"), "app.linkLabel")
            url, host = public_url(app.get("url"))
            status = app.get("status", "ready")
            if status not in STATUSES:
                raise ValueError("Application status must be ready, setup or unavailable")
            badge = f'<span class="status status-{status}">{STATUSES[status]}</span>' if STATUSES[status] else ""
            subtitle_html = f'<p class="app-subtitle"><bdi>{subtitle}</bdi></p>' if subtitle else ""
            note_html = f'<p class="app-note">{note}</p>' if note else ""
            cards.append(f'''<article class="app-card" aria-labelledby="app-{app_key}">
  <div class="app-heading"><h3 id="app-{app_key}">{name}</h3>{badge}</div>
  {subtitle_html}
  <p class="app-description">{summary}</p>
  {note_html}
  <div class="app-bottom"><bdi class="app-host" dir="ltr">{host}</bdi>
    <a class="app-link" href="{url}" aria-label="{link_label}: {name}">{link_label} <span aria-hidden="true">↗</span></a>
  </div>
</article>''')
            count += 1
        number_fa = str(number).zfill(2).translate(PERSIAN_DIGITS)
        sections.append(f'''<section class="directory-group" aria-labelledby="group-{key}">
  <div class="group-heading"><span class="group-number" aria-hidden="true">{number_fa}</span><div>
    <h2 id="group-{key}">{title}</h2><p>{description}</p>
  </div></div>
  <div class="app-grid">{"".join(cards)}</div>
</section>''')
    template = Template((ROOT / "template.html").read_text(encoding="utf-8"))
    return template.substitute(groups="\n".join(sections), app_count=str(count).translate(PERSIAN_DIGITS)), count


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", required=True, type=Path)
    parser.add_argument("--assets", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    catalog = json.loads(args.catalog.read_text(encoding="utf-8-sig"))
    html, count = render(catalog)
    for name in ASSETS:
        if not (args.assets / name).is_file():
            parser.error(f"Required approved asset is missing: {name}")
    output = args.output.resolve()
    if output == args.assets.resolve() or output == args.catalog.resolve().parent:
        parser.error("Use a separate output directory")
    (output / "assets").mkdir(parents=True, exist_ok=True)
    for name in ASSETS:
        shutil.copyfile(args.assets / name, output / "assets" / name)
    shutil.copyfile(ROOT / "directory.css", output / "directory.css")
    (output / "index.html").write_text(html, encoding="utf-8", newline="\n")
    print(json.dumps({"output": str(output), "applications": count, "runtimeScripts": 0}, ensure_ascii=False))


if __name__ == "__main__":
    main()
