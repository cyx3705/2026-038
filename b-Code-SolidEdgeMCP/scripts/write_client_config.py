"""Generate client snippets for this checkout; never edit global client settings."""

import argparse
import json
import sys
from pathlib import Path


def write_config(project: Path, python: Path):
    folder = project / "config"
    folder.mkdir(exist_ok=True)
    output = (project / "data").as_posix()
    command = python.resolve().as_posix()
    args = ["-m", "solid_edge_mcp", "--output-dir", output]
    config = {
        "mcpServers": {"solid-edge": {"command": command, "args": args, "env": {"PYTHONUTF8": "1"}}}
    }
    (folder / "mcp.json").write_text(
        json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    lines = [
        "# Generated for this checkout. Regenerate after moving the project.",
        "# Merge this section into your Codex config.toml.",
        "[mcp_servers.solid-edge]",
        f"command = {json.dumps(command, ensure_ascii=False)}",
        f"args = {json.dumps(args, ensure_ascii=False)}",
        "startup_timeout_sec = 30",
        "tool_timeout_sec = 150",
        "",
        "[mcp_servers.solid-edge.env]",
        'PYTHONUTF8 = "1"',
        "",
    ]
    (folder / "codex.toml").write_text("\n".join(lines), encoding="utf-8")
    print(f"Client configuration written to {folder}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--python", type=Path, default=Path(sys.executable))
    args = parser.parse_args()
    write_config(Path(__file__).resolve().parents[1], args.python)
