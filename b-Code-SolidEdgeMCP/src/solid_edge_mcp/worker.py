"""Keep all COM proxies and calls on one long-lived STA thread."""

import asyncio
import gc
import json
import queue
import sys
import threading
from concurrent.futures import Future

from .errors import EdgeError


class ComWorker:
    def __init__(self, factory, *, timeout: float = 120, use_com: bool = True):
        self.factory = factory
        self.timeout = timeout
        self.use_com = use_com
        self._jobs = queue.Queue()
        self._lock = threading.Lock()
        self._thread = None
        self._pending = None
        self._closed = False

    async def call(self, method: str, **kwargs):
        with self._lock:
            if self._closed:
                raise EdgeError("The COM worker has stopped. Restart the MCP server.")
            if self._pending is not None and not self._pending.done():
                raise EdgeError(
                    "Solid Edge is still processing the previous call. Do not retry writes."
                )
            if self._thread is None:
                self._thread = threading.Thread(
                    target=self._run, name="solid-edge-sta", daemon=True
                )
                self._thread.start()
            future = Future()
            self._pending = future
            self._jobs.put((future, method, kwargs))
        wrapped = asyncio.wrap_future(future)
        # A caller can time out/cancel before COM reports an error. Consume it without hiding
        # errors from a caller that is still awaiting the Future.
        wrapped.add_done_callback(lambda done: None if done.cancelled() else done.exception())
        try:
            # Cancellation must not cancel an already dispatched COM mutation.
            return await asyncio.wait_for(asyncio.shield(wrapped), timeout=self.timeout)
        except TimeoutError as exc:
            raise EdgeError(
                "Solid Edge call timed out; it may still complete. Inspect for a CAD dialog, "
                "then query the document before retrying any write."
            ) from exc

    def _run(self):
        backend = None
        com = None
        startup_error = None
        try:
            if self.use_com:
                if sys.platform != "win32":
                    raise EdgeError("Solid Edge automation requires Windows and pywin32.")
                import pythoncom

                pythoncom.CoInitializeEx(pythoncom.COINIT_APARTMENTTHREADED)
                com = pythoncom
            backend = self.factory()
        except Exception as exc:
            startup_error = str(exc)
        try:
            while True:
                try:
                    job = self._jobs.get(timeout=0.05)
                except queue.Empty:
                    if com:
                        com.PumpWaitingMessages()
                    continue
                if job is None:
                    break
                future, method, kwargs = job
                try:
                    if startup_error:
                        raise EdgeError(startup_error)
                    result = getattr(backend, method)(**kwargs)
                    # Reject accidental COM objects or NaN before crossing the apartment boundary.
                    json.dumps(result, allow_nan=False)
                    future.set_result(result)
                except Exception as exc:
                    # Do not carry COM references in exception tracebacks across apartments.
                    hresult = getattr(exc, "hresult", None)
                    prefix = f"COM 0x{hresult & 0xFFFFFFFF:08X}: " if hresult else ""
                    future.set_exception(EdgeError(prefix + str(exc)))
                if com:
                    com.PumpWaitingMessages()
        finally:
            if backend is not None:
                backend.disconnect()
            backend = None
            gc.collect()
            if com:
                com.CoUninitialize()

    def close(self):
        with self._lock:
            self._closed = True
            self._jobs.put(None)
        if self._thread:
            self._thread.join(timeout=2)
