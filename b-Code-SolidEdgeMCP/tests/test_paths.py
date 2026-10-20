import pytest

from solid_edge_mcp.errors import EdgeError
from solid_edge_mcp.paths import OutputPaths


@pytest.mark.parametrize("name", ["../escape.par", "nested/../../escape.par", "file.exe"])
def test_reject_invalid_output(tmp_path, name):
    paths = OutputPaths(tmp_path / "output")
    with pytest.raises(EdgeError):
        paths.target(name, {".par"})


def test_reject_absolute_path_outside_root(tmp_path):
    paths = OutputPaths(tmp_path / "output")
    with pytest.raises(EdgeError, match="inside"):
        paths.target(str(tmp_path / "outside.par"), {".par"})


def test_existing_file_requires_explicit_overwrite(tmp_path):
    paths = OutputPaths(tmp_path)
    target = paths.target("sub/part.PAR", {".par"})
    target.write_bytes(b"original")
    with pytest.raises(EdgeError, match="already exists"):
        paths.target("sub/part.PAR", {".par"})
    assert target.read_bytes() == b"original"
    assert paths.target("sub/part.PAR", {".par"}, True) == target


def test_nonempty_file_verification(tmp_path):
    target = tmp_path / "empty.par"
    target.touch()
    with pytest.raises(EdgeError, match="nonempty"):
        OutputPaths.verify(target)
    target.write_bytes(b"native")
    assert OutputPaths.verify(target)["size_bytes"] == 6
