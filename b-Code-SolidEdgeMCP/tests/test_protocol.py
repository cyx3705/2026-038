import json
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


async def test_real_stdio_handshake_tools_validation_and_errors(tmp_path):
    params = StdioServerParameters(
        command=sys.executable,
        args=["-m", "solid_edge_mcp", "--output-dir", str(tmp_path)],
    )
    async with stdio_client(params) as (read, write), ClientSession(read, write) as session:
        init = await session.initialize()
        assert init.serverInfo.name == "solid-edge-mcp"
        tools = {tool.name: tool for tool in (await session.list_tools()).tools}
        assert len(tools) == 23
        assert tools["se_geometry"].annotations.readOnlyHint
        assert tools["se_close_document"].annotations.destructiveHint
        schema = tools["se_create_box"].inputSchema["properties"]
        assert schema["width_mm"]["exclusiveMinimum"] == 0
        for length in (-1, 0):
            invalid = await session.call_tool(
                "se_create_box",
                {
                    "document_id": "unused",
                    "width_mm": length,
                    "depth_mm": 10,
                    "height_mm": 5,
                },
            )
            assert invalid.isError
        wrong_choice = await session.call_tool(
            "se_extrude",
            {
                "document_id": "unused",
                "profile_id": "unused",
                "distance_mm": 10,
                "operation": "intersect",
            },
        )
        assert wrong_choice.isError
        disconnected = await session.call_tool("se_get_document", {"document_id": "unknown"})
        assert disconnected.isError
        if sys.platform == "win32":
            status = await session.call_tool("se_status")
            assert not status.isError
            payload = status.structuredContent or json.loads(status.content[0].text)
            assert payload["connected"] is False
        resource = await session.read_resource("solidedge://guide")
        assert "millimeters" in resource.contents[0].text
