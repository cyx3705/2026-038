import argparse
import json
import logging
import platform
import sys
from importlib.metadata import version
from pathlib import Path


def doctor():
    result = {"python": platform.python_version(), "platform": sys.platform, "mcp": version("mcp")}
    result["supported_platform"] = sys.platform == "win32"
    result["registered"] = False
    if sys.platform == "win32":
        import winreg

        try:
            with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, r"SolidEdge.Application\CLSID") as key:
                result["clsid"] = winreg.QueryValue(key, None)
                result["registered"] = True
        except OSError as exc:
            result["error"] = str(exc)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["registered"] else 1


def main():
    parser = argparse.ArgumentParser(description="Solid Edge MCP server (stdio)")
    parser.add_argument("--output-dir", type=Path, help="Root directory for generated CAD files")
    parser.add_argument(
        "--doctor", action="store_true", help="Check environment without starting CAD"
    )
    args = parser.parse_args()
    if args.doctor:
        raise SystemExit(doctor())
    logging.basicConfig(level=logging.WARNING, stream=sys.stderr)
    from .server import create_server

    create_server(args.output_dir).run(transport="stdio")


if __name__ == "__main__":
    main()
