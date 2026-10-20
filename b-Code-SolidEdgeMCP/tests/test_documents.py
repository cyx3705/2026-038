from types import SimpleNamespace
from unittest.mock import Mock

import pytest

from solid_edge_mcp.backend import SolidEdgeBackend
from solid_edge_mcp.errors import EdgeError


def collection(items):
    return SimpleNamespace(Count=len(items), Item=lambda index: items[index - 1])


def test_closed_document_id_never_targets_another_active_document(tmp_path):
    backend = SolidEdgeBackend(tmp_path)
    original = SimpleNamespace(_oleobj_=object())
    unrelated = SimpleNamespace(_oleobj_=object())
    backend.app = SimpleNamespace(Documents=collection([unrelated]), ActiveDocument=unrelated)
    backend.documents["original"] = original
    backend.profiles["sketch"] = ("original", object())
    with pytest.raises(EdgeError, match="closed"):
        backend._doc("original")
    assert "original" not in backend.documents
    assert "sketch" not in backend.profiles


def test_unsaved_document_is_not_closed_implicitly(tmp_path):
    backend = SolidEdgeBackend(tmp_path)
    doc = SimpleNamespace(_oleobj_=object(), Dirty=True, Close=Mock())
    backend.app = SimpleNamespace(Documents=collection([doc]))
    backend.documents["part"] = doc
    with pytest.raises(EdgeError, match="unsaved changes"):
        backend.close_document("part")
    doc.Close.assert_not_called()


def test_alerts_are_restored_after_failure(tmp_path):
    backend = SolidEdgeBackend(tmp_path)
    backend.app = SimpleNamespace(DisplayAlerts=True)
    with pytest.raises(RuntimeError), backend._no_alerts():
        assert backend.app.DisplayAlerts is False
        raise RuntimeError("translation failed")
    assert backend.app.DisplayAlerts is True


def test_profile_from_another_document_is_rejected_before_com_mutation(tmp_path):
    backend = SolidEdgeBackend(tmp_path)
    doc = SimpleNamespace(_oleobj_=object(), Type=1)
    backend.app = SimpleNamespace(Documents=collection([doc]))
    backend.documents["part"] = doc
    backend.profiles["sketch"] = ("different-part", object())
    with pytest.raises(EdgeError, match="different document"):
        backend.extrude("part", "sketch", 10)
