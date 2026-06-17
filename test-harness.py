#!/usr/bin/env python3
"""Drive the MCP server over stdio: initialize -> tools/list -> tools/call."""
import json, subprocess, sys, os

dll = sys.argv[1]
file_to_analyze = sys.argv[2]
content = open(file_to_analyze).read()

msgs = [
    {"jsonrpc":"2.0","id":1,"method":"initialize",
     "params":{"protocolVersion":"2024-11-05","capabilities":{},
               "clientInfo":{"name":"harness","version":"1"}}},
    {"jsonrpc":"2.0","method":"notifications/initialized"},
    {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}},
    {"jsonrpc":"2.0","id":3,"method":"tools/call",
     "params":{"name":"analyze_csharp",
               "arguments":{"fileContent":content,
                            "projectKey":"Aptem.ActivityLogging"}}},
]
stdin = "".join(json.dumps(m) + "\n" for m in msgs)

p = subprocess.run(["dotnet", dll], input=stdin, capture_output=True, text=True, timeout=180)
for line in p.stdout.splitlines():
    try:
        obj = json.loads(line)
    except Exception:
        continue
    if obj.get("id") == 2:
        tools = [t["name"] for t in obj["result"]["tools"]]
        print("TOOLS:", tools)
    if obj.get("id") == 3:
        # tool result text is in result.content[0].text
        txt = obj["result"]["content"][0]["text"]
        print("RESULT:")
        print(json.dumps(json.loads(txt), indent=2))
