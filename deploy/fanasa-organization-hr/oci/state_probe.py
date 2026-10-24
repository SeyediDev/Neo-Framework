#!/usr/bin/env python3
"""Read-only SQLite evidence for isolated synthetic adapter volumes."""
import argparse
import hashlib
import json
from pathlib import Path
import sqlite3


def inspect(mode):
    directory = Path("/state/fabric" if mode == "web" else "/state/workbench")
    databases = sorted(path for path in directory.glob("*") if path.suffix in {".db", ".sqlite", ".sqlite3"})
    if len(databases) != 1 or databases[0].is_symlink():
        raise ValueError("Exactly one synthetic application database is required")
    connection = sqlite3.connect(databases[0].as_uri() + "?mode=ro", uri=True, timeout=3)
    try:
        if connection.execute("PRAGMA integrity_check").fetchone()[0] != "ok":
            raise ValueError("SQLite integrity check failed")
        if connection.execute("PRAGMA foreign_key_check").fetchall():
            raise ValueError("SQLite foreign key check failed")
        tables = [row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
        if not tables or len(tables) > 1000:
            raise ValueError("Synthetic application schema is absent or excessive")
        digest, total = hashlib.sha256(), 0
        for line in connection.iterdump():
            data = (line + "\n").encode()
            total += len(data)
            if total > 16 * 1024 * 1024:
                raise ValueError("Synthetic database evidence exceeds limit")
            digest.update(data)
        counts = {name: connection.execute('SELECT COUNT(*) FROM "' + name.replace('"', '""') + '"').fetchone()[0] for name in tables}
        return {"mode": mode, "integrity": "ok", "foreignKeys": 0, "tables": len(tables),
                "rows": counts, "logicalSha256": digest.hexdigest()}
    finally:
        connection.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["web", "workbench"])
    print(json.dumps(inspect(parser.parse_args().mode)))
