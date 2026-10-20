from pathlib import Path

from .errors import EdgeError


class OutputPaths:
    """Confine generated files to the configured project data directory."""

    def __init__(self, root: Path):
        self.root = root.expanduser().resolve()

    def target(self, value: str, suffixes: set[str], overwrite: bool = False) -> Path:
        supplied = Path(value)
        path = (supplied if supplied.is_absolute() else self.root / supplied).resolve()
        if not path.is_relative_to(self.root):
            raise EdgeError(f"Output must be inside {self.root}")
        if path.suffix.lower() not in suffixes:
            raise EdgeError(f"Expected extension: {', '.join(sorted(suffixes))}")
        if path.exists() and (not overwrite or not path.is_file()):
            raise EdgeError(f"Output already exists: {path}. Use overwrite=true explicitly.")
        path.parent.mkdir(parents=True, exist_ok=True)
        return path

    @staticmethod
    def verify(path: Path) -> dict:
        if not path.is_file() or path.stat().st_size == 0:
            raise EdgeError(f"Solid Edge did not produce a nonempty file: {path}")
        return {"path": str(path), "size_bytes": path.stat().st_size}
