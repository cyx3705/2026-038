import asyncio
import os
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Annotated, Literal

from mcp.server.fastmcp import FastMCP, Image
from mcp.server.fastmcp.exceptions import ToolError
from mcp.types import ToolAnnotations
from pydantic import Field

from .backend import SolidEdgeBackend
from .errors import EdgeError
from .worker import ComWorker

Length = Annotated[float, Field(gt=0, le=1_000_000, allow_inf_nan=False)]
Coordinate = Annotated[float, Field(ge=-1_000_000, le=1_000_000, allow_inf_nan=False)]
Finite = Annotated[float, Field(allow_inf_nan=False)]
Index = Annotated[int, Field(ge=1)]
READ = ToolAnnotations(readOnlyHint=True, destructiveHint=False, openWorldHint=False)
WRITE = ToolAnnotations(readOnlyHint=False, destructiveHint=True, openWorldHint=False)
CREATE = ToolAnnotations(readOnlyHint=False, destructiveHint=False, openWorldHint=False)

INSTRUCTIONS = """Control Siemens Solid Edge through its Windows COM API.
Call se_connect, then se_new_part or se_list_documents. Use the returned document_id
for every document operation. IDs expire on disconnect, close, or server restart.
All modeling lengths are millimeters; variable angles are degrees. Plane and model
indices are 1-based. Query se_reference_planes; sketch coordinates are plane-local.
This release models ordered, single-body .par parts. The first extrusion creates a
base solid; later add/cut extrusions modify it. A profile is consumed after extrusion.
Verify geometry after changing dimensions and inspect the returned feature status.
Writes are confined to SOLIDEDGE_MCP_OUTPUT_DIR. Existing files require overwrite=true.
Errors are MCP isError responses. After a COM error or timeout, inspect the document
before retrying writes: COM modeling is not transactional and may have changed it.
No tool runs arbitrary Python, shell commands, or undocumented COM member chains.
"""


def create_server(output_root: Path | None = None, *, worker=None):
    root = output_root or Path(os.environ.get("SOLIDEDGE_MCP_OUTPUT_DIR", Path.cwd() / "data"))
    com = worker or ComWorker(lambda: SolidEdgeBackend(root))

    @asynccontextmanager
    async def lifespan(_server):
        try:
            yield {}
        finally:
            await asyncio.to_thread(com.close)

    server = FastMCP("solid-edge-mcp", instructions=INSTRUCTIONS, lifespan=lifespan)

    async def invoke(method, **kwargs):
        try:
            return await com.call(method, **kwargs)
        except EdgeError as exc:
            raise ToolError(str(exc)) from None

    @server.tool(annotations=READ)
    async def se_status() -> dict:
        """Get server connection, Solid Edge version, and output directory; never launch CAD."""
        return await invoke("status")

    @server.tool(annotations=CREATE)
    async def se_connect(start_if_needed: bool = True, visible: bool = True) -> dict:
        """Attach to Solid Edge; optionally launch it. Keep CAD visible for interactive work."""
        return await invoke("connect", start_if_needed=start_if_needed, visible=visible)

    @server.tool(annotations=CREATE)
    async def se_disconnect() -> dict:
        """Release COM references and invalidate IDs. Leave Solid Edge and documents open."""
        return await invoke("disconnect")

    @server.tool(annotations=READ)
    async def se_list_documents() -> dict:
        """List open documents and their stable IDs for this server connection."""
        return await invoke("list_documents")

    @server.tool(annotations=READ)
    async def se_get_document(document_id: str) -> dict:
        """Read document name, path, type, and unsaved-change status."""
        return await invoke("get_document", document_id=document_id)

    @server.tool(annotations=CREATE)
    async def se_new_part() -> dict:
        """Create a new empty ordered part using Solid Edge's default part template."""
        return await invoke("new_part")

    @server.tool(annotations=CREATE)
    async def se_open_document(path: str) -> dict:
        """Open an existing .par file. Supply its absolute Windows path."""
        return await invoke("open_document", path=path)

    @server.tool(annotations=READ)
    async def se_reference_planes(document_id: str) -> dict:
        """List actual reference plane names and 1-based indices in this part."""
        return await invoke("reference_planes", document_id=document_id)

    @server.tool(annotations=CREATE)
    async def se_create_rectangle(
        document_id: str,
        width_mm: Length,
        height_mm: Length,
        x_mm: Coordinate = 0,
        y_mm: Coordinate = 0,
        plane_index: Index = 1,
    ) -> dict:
        """Create a rectangle from its lower-left corner, using local plane coordinates."""
        return await invoke(
            "rectangle",
            document_id=document_id,
            width_mm=width_mm,
            height_mm=height_mm,
            x_mm=x_mm,
            y_mm=y_mm,
            plane_index=plane_index,
        )

    @server.tool(annotations=CREATE)
    async def se_create_circle(
        document_id: str,
        radius_mm: Length,
        x_mm: Coordinate = 0,
        y_mm: Coordinate = 0,
        plane_index: Index = 1,
    ) -> dict:
        """Create a closed circle profile. x_mm/y_mm specify its center on the local plane."""
        return await invoke(
            "circle",
            document_id=document_id,
            radius_mm=radius_mm,
            x_mm=x_mm,
            y_mm=y_mm,
            plane_index=plane_index,
        )

    @server.tool(annotations=WRITE)
    async def se_extrude(
        document_id: str,
        profile_id: str,
        distance_mm: Length,
        operation: Literal["add", "cut"] = "add",
        direction: Literal["normal", "reverse"] = "normal",
    ) -> dict:
        """Extrude a closed profile and verify volume change. Consumes the profile on completion."""
        return await invoke(
            "extrude",
            document_id=document_id,
            profile_id=profile_id,
            distance_mm=distance_mm,
            operation=operation,
            direction=direction,
        )

    @server.tool(annotations=CREATE)
    async def se_create_box(
        document_id: str,
        width_mm: Length,
        depth_mm: Length,
        height_mm: Length,
        plane_index: Index = 1,
    ) -> dict:
        """Create a box in an EMPTY part; width/depth follow plane axes, height its normal."""
        return await invoke(
            "box",
            document_id=document_id,
            width_mm=width_mm,
            depth_mm=depth_mm,
            height_mm=height_mm,
            plane_index=plane_index,
        )

    @server.tool(annotations=CREATE)
    async def se_create_cylinder(
        document_id: str,
        radius_mm: Length,
        height_mm: Length,
        plane_index: Index = 1,
    ) -> dict:
        """Create a cylinder in an EMPTY part, centered at the plane origin."""
        return await invoke(
            "cylinder",
            document_id=document_id,
            radius_mm=radius_mm,
            height_mm=height_mm,
            plane_index=plane_index,
        )

    @server.tool(annotations=READ)
    async def se_geometry(document_id: str) -> dict:
        """Measure solid volume (mm^3), face count, and world-coordinate bounding box (mm)."""
        return await invoke("geometry", document_id=document_id)

    @server.tool(annotations=READ)
    async def se_features(document_id: str) -> dict:
        """List ordered features, model indices, and Solid Edge feature status codes."""
        return await invoke("features", document_id=document_id)

    @server.tool(annotations=READ)
    async def se_list_variables(document_id: str) -> dict:
        """List numeric variables/dimensions, exact names, formulas, values, and units."""
        return await invoke("list_variables", document_id=document_id)

    @server.tool(annotations=WRITE)
    async def se_set_extrusion_depth(
        document_id: str,
        feature_name: str,
        depth_mm: Length,
    ) -> dict:
        """Change a finite extrusion/cutout depth by feature name; read back depth and geometry."""
        return await invoke(
            "set_extrusion_depth",
            document_id=document_id,
            feature_name=feature_name,
            depth_mm=depth_mm,
        )

    @server.tool(annotations=WRITE)
    async def se_set_variable(
        document_id: str,
        name: str,
        value: Finite,
        unit: Literal["mm", "deg", "raw"],
    ) -> dict:
        """Set an existing variable and read it back; unit must match se_list_variables exactly."""
        return await invoke(
            "set_variable", document_id=document_id, name=name, value=value, unit=unit
        )

    @server.tool(annotations=WRITE)
    async def se_save_document(
        document_id: str,
        path: str | None = None,
        overwrite: bool = False,
    ) -> dict:
        """Save a native .par inside the output root. Existing files need overwrite=true."""
        return await invoke(
            "save_document", document_id=document_id, path=path, overwrite=overwrite
        )

    @server.tool(annotations=WRITE)
    async def se_export_step(document_id: str, path: str, overwrite: bool = False) -> dict:
        """Export .step/.stp, verify a nonempty file, and retain the native part document path."""
        return await invoke("export_step", document_id=document_id, path=path, overwrite=overwrite)

    @server.tool(annotations=CREATE)
    async def se_set_view(
        document_id: str,
        orientation: Literal["Iso", "Front", "Top", "Right"] = "Iso",
    ) -> dict:
        """Activate this part, apply a named view, and fit the geometry in the window."""
        return await invoke("set_view", document_id=document_id, orientation=orientation)

    @server.tool(annotations=WRITE)
    async def se_capture_viewport(
        document_id: str,
        path: str = "viewport.png",
        width: Annotated[int, Field(ge=64, le=4096)] = 1280,
        height: Annotated[int, Field(ge=64, le=4096)] = 960,
        overwrite: bool = False,
    ) -> Image:
        """Save a PNG of this part's current view and return the image to the AI client."""
        result = await invoke(
            "capture_viewport",
            document_id=document_id,
            path=path,
            width=width,
            height=height,
            overwrite=overwrite,
        )
        return Image(path=result["path"])

    @server.tool(annotations=WRITE)
    async def se_close_document(document_id: str, discard_changes: bool = False) -> dict:
        """Close this document. Refuse unsaved changes unless discard_changes is explicitly true."""
        return await invoke(
            "close_document", document_id=document_id, discard_changes=discard_changes
        )

    @server.resource("solidedge://guide")
    def guide() -> str:
        """Units, scope, workflow, and failure semantics."""
        return INSTRUCTIONS

    return server
