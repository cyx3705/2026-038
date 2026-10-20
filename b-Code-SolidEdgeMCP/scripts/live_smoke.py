"""Exercise a real stdio MCP client against a licensed Solid Edge instance.

Creates only new parts, writes to a new run directory, and closes only its own documents.
"""

import argparse
import asyncio
import json
import math
import sys
from datetime import datetime
from pathlib import Path

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def run(output_dir: Path, keep_open: bool):
    output_dir.mkdir(parents=True, exist_ok=False)
    report = {"passed": False, "steps": []}
    created = []
    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "solid_edge_mcp", "--output-dir", str(output_dir.resolve())],
    )
    try:
        async with stdio_client(params) as (read, write), ClientSession(read, write) as session:
            await session.initialize()
            tool_list = await session.list_tools()
            report["tool_count"] = len(tool_list.tools)

            async def call(tool_name, **kwargs):
                result = await session.call_tool(tool_name, kwargs)
                if result.isError:
                    raise RuntimeError(f"{tool_name}: {result.content}")
                data = result.structuredContent
                if data is None:
                    texts = [item.text for item in result.content if item.type == "text"]
                    data = (
                        json.loads(texts[0])
                        if texts
                        else {"content_types": [item.type for item in result.content]}
                    )
                print(
                    json.dumps({"tool": tool_name, "result": data}, ensure_ascii=False), flush=True
                )
                report["steps"].append({"tool": tool_name, "arguments": kwargs, "result": data})
                return data

            try:
                await call("se_status")
                report["connection"] = await call("se_connect")
                part = await call("se_new_part")
                doc_id = part["document_id"]
                created.append(doc_id)
                await call("se_reference_planes", document_id=doc_id)
                await call(
                    "se_create_box", document_id=doc_id, width_mm=100, depth_mm=60, height_mm=20
                )
                geometry = await call("se_geometry", document_id=doc_id)
                assert math.isclose(geometry["total_volume_mm3"], 120000, rel_tol=1e-7)
                assert sorted(geometry["models"][0]["size_mm"]) == [20, 60, 100]
                variables = await call("se_list_variables", document_id=doc_id)
                accuracy = next(
                    v for v in variables["variables"] if v["name"] == "PhysicalProperties_Accuracy"
                )
                await call(
                    "se_set_variable",
                    document_id=doc_id,
                    name=accuracy["name"],
                    value=0.98,
                    unit="raw",
                )
                features = await call("se_features", document_id=doc_id)
                await call(
                    "se_set_extrusion_depth",
                    document_id=doc_id,
                    feature_name=features["features"][0]["name"],
                    depth_mm=25,
                )
                geometry = await call("se_geometry", document_id=doc_id)
                assert math.isclose(geometry["total_volume_mm3"], 150000, rel_tol=1e-7)
                circle = await call(
                    "se_create_circle", document_id=doc_id, radius_mm=6, x_mm=50, y_mm=30
                )
                await call(
                    "se_extrude",
                    document_id=doc_id,
                    profile_id=circle["profile_id"],
                    distance_mm=25,
                    operation="cut",
                )
                geometry = await call("se_geometry", document_id=doc_id)
                expected = 150000 - math.pi * 6**2 * 25
                assert math.isclose(geometry["total_volume_mm3"], expected, rel_tol=1e-7)
                await call("se_features", document_id=doc_id)
                await call("se_set_view", document_id=doc_id)
                await call("se_capture_viewport", document_id=doc_id, path="plate-with-hole.png")
                await call("se_save_document", document_id=doc_id, path="plate-with-hole.par")
                await call("se_export_step", document_id=doc_id, path="plate-with-hole.step")
                assert (
                    (output_dir / "plate-with-hole.step").read_bytes().startswith(b"ISO-10303-21;")
                )
                # Overwrite guards and document reopening exercise persistence through MCP.
                duplicate = await session.call_tool(
                    "se_export_step",
                    {
                        "document_id": doc_id,
                        "path": "plate-with-hole.step",
                    },
                )
                assert duplicate.isError
                await call("se_close_document", document_id=doc_id)
                created.remove(doc_id)
                reopened = await call(
                    "se_open_document", path=str((output_dir / "plate-with-hole.par").resolve())
                )
                doc_id = reopened["document_id"]
                created.append(doc_id)
                await call("se_get_document", document_id=doc_id)
                listing = await call("se_list_documents")
                assert doc_id in {d["document_id"] for d in listing["documents"]}
                restored = await call("se_geometry", document_id=doc_id)
                assert math.isclose(restored["total_volume_mm3"], expected, rel_tol=1e-7)
                # A second part exercises IDs while another document is active.
                cylinder = await call("se_new_part")
                cylinder_id = cylinder["document_id"]
                created.append(cylinder_id)
                await call(
                    "se_create_cylinder", document_id=cylinder_id, radius_mm=10, height_mm=30
                )
                cyl_geometry = await call("se_geometry", document_id=cylinder_id)
                assert math.isclose(
                    cyl_geometry["total_volume_mm3"], math.pi * 100 * 30, rel_tol=1e-7
                )
                rectangle = await call(
                    "se_create_rectangle", document_id=cylinder_id, width_mm=5, height_mm=5
                )
                assert rectangle["validation_passed"]
                added = await call(
                    "se_extrude",
                    document_id=cylinder_id,
                    profile_id=rectangle["profile_id"],
                    distance_mm=40,
                )
                assert math.isclose(
                    added["geometry"]["total_volume_mm3"], math.pi * 100 * 30 + 250, rel_tol=1e-7
                )
                box_again = await call("se_geometry", document_id=doc_id)
                assert math.isclose(box_again["total_volume_mm3"], expected, rel_tol=1e-7)
                await call("se_close_document", document_id=cylinder_id, discard_changes=True)
                created.remove(cylinder_id)
                await call("se_set_view", document_id=doc_id)
                for orientation in ("Front", "Top", "Right", "Iso"):
                    await call("se_set_view", document_id=doc_id, orientation=orientation)
                report["unique_tools_exercised"] = sorted({s["tool"] for s in report["steps"]})
                report["passed"] = True
            finally:
                if not (keep_open and report["passed"]):
                    for document_id in created:
                        await session.call_tool(
                            "se_close_document",
                            {
                                "document_id": document_id,
                                "discard_changes": True,
                            },
                        )
                disconnected = await session.call_tool("se_disconnect")
                assert not disconnected.isError
    finally:
        (output_dir / "verification.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path("data") / datetime.now().strftime("smoke-%Y%m%d-%H%M%S"),
    )
    parser.add_argument("--keep-open", action="store_true")
    args = parser.parse_args()
    asyncio.run(run(args.output_dir, args.keep_open))


if __name__ == "__main__":
    main()
