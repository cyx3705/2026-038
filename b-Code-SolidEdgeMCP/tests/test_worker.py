import asyncio
import threading

import pytest

from solid_edge_mcp.errors import EdgeError
from solid_edge_mcp.worker import ComWorker


class Backend:
    def __init__(self):
        self.created_on = threading.get_ident()
        self.closed_on = None
        self.release = threading.Event()
        self.calls = 0

    def identity(self):
        return {"thread": threading.get_ident()}

    def slow(self):
        self.calls += 1
        assert self.release.wait(timeout=3)
        return {"finished": True}

    def broken(self):
        raise ValueError("backend failure")

    def leak(self):
        return {"proxy": object()}

    def disconnect(self):
        self.closed_on = threading.get_ident()


async def test_lifecycle_runs_on_one_other_thread():
    instances = []

    def factory():
        backend = Backend()
        instances.append(backend)
        return backend

    worker = ComWorker(factory, use_com=False)
    try:
        first = await worker.call("identity")
        second = await worker.call("identity")
        assert first == second
        assert first["thread"] != threading.get_ident()
        assert first["thread"] == instances[0].created_on
        with pytest.raises(EdgeError, match="backend failure"):
            await worker.call("broken")
        assert await worker.call("identity") == first
    finally:
        worker.close()
    assert instances[0].closed_on == first["thread"]
    with pytest.raises(EdgeError, match="stopped"):
        await worker.call("identity")


async def test_timeout_does_not_retry_or_cancel_a_write():
    backend = Backend()
    worker = ComWorker(lambda: backend, timeout=0.05, use_com=False)
    try:
        with pytest.raises(EdgeError, match="may still complete"):
            await worker.call("slow")
        with pytest.raises(EdgeError, match="previous call"):
            await worker.call("identity")
        assert backend.calls == 1
        backend.release.set()
        await asyncio.to_thread(worker._pending.result, 2)
        assert "thread" in await worker.call("identity")
        assert backend.calls == 1
    finally:
        backend.release.set()
        worker.close()


async def test_non_json_result_does_not_cross_worker_boundary():
    worker = ComWorker(Backend, use_com=False)
    try:
        with pytest.raises(EdgeError, match="JSON serializable"):
            await worker.call("leak")
    finally:
        worker.close()


async def test_startup_failure_is_returned_to_all_calls():
    def failed_factory():
        raise RuntimeError("no COM runtime")

    worker = ComWorker(failed_factory, use_com=False)
    try:
        for _ in range(2):
            with pytest.raises(EdgeError, match="no COM runtime"):
                await worker.call("identity")
    finally:
        worker.close()
