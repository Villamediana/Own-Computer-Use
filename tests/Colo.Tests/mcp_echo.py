# Servidor MCP mínimo por stdio para los tests.
import json, sys
for line in sys.stdin:
    msg = json.loads(line)
    if "id" not in msg:
        continue
    m, i = msg["method"], msg["id"]
    if m == "initialize":
        r = {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "echo", "version": "1"}}
    elif m == "tools/list":
        r = {"tools": [
            {"name": "echo", "description": "Repite el texto", "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}}},
            {"name": "add", "description": "Suma", "inputSchema": {"type": "object", "properties": {"a": {"type": "number"}, "b": {"type": "number"}}}},
        ]}
    elif m == "tools/call":
        a = msg["params"]["arguments"]
        if msg["params"]["name"] == "echo":
            r = {"content": [{"type": "text", "text": "eco: " + a.get("text", "")}]}
        else:
            r = {"content": [{"type": "text", "text": str(a["a"] + a["b"])}], "isError": False}
    else:
        print(json.dumps({"jsonrpc": "2.0", "id": i, "error": {"code": -32601, "message": "no"}}), flush=True)
        continue
    print(json.dumps({"jsonrpc": "2.0", "id": i, "result": r}), flush=True)
