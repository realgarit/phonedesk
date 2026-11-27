#!/usr/bin/env python3
"""Starts the published MCP server and checks that it answers over stdio.

Usage: smoke-test-mcp-server.py <publish dir>

The test needs no tenant and no sign-in: it runs the protocol handshake, lists the tools, and
calls two tools that work offline. It fails when the server does not start, writes anything
except protocol messages to stdout, or lists fewer tools than expected.
"""
import json
import os
import subprocess
import sys
import tempfile

EXPECTED_TOOLS = {
    "session_status", "check_modules", "connect_teams", "connect_graph", "disconnect",
    "configuration_get", "configuration_update", "tenant_overview", "run_health_check",
    "backup_tenant", "compare_with_backup", "guided_setup_status", "guided_setup_run_step",
    "create_call_queue", "create_auto_attendant", "delete_call_queue", "bulk_setup", "audit_history",
    "list_holiday_schedules", "holiday_call_handling", "copy_holiday_call_handling",
    "backup_holiday_call_handling", "list_audio_files",
    "tenant_identity", "restart_sign_in", "compare_holiday_list", "holiday_change_table",
    "list_phone_numbers", "set_phone_number_assignment", "remove_phone_number_assignment",
    "edit_holiday_schedule_dates", "replace_holiday_greeting", "retire_holiday_schedules", "rollout_holiday_call_handling",
}


def main() -> int:
    publish_dir = sys.argv[1]
    executable = next(
        (path for path in (os.path.join(publish_dir, name) for name in ("phonedesk-mcp.exe", "phonedesk-mcp"))
         if os.path.isfile(path)),
        None)
    if executable is None:
        print(f"No MCP server executable in {publish_dir}", file=sys.stderr)
        return 1

    environment = dict(os.environ, PHONEDESK_MCP_DATA_DIR=tempfile.mkdtemp(prefix="phonedesk-mcp-smoke-"))
    server = subprocess.Popen(
        [os.path.abspath(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
        env=environment)

    def send(message):
        server.stdin.write((json.dumps(message) + "\n").encode())
        server.stdin.flush()

    def read(expected_id):
        while True:
            line = server.stdout.readline()
            if not line:
                raise RuntimeError("The server closed stdout before it answered.")
            try:
                message = json.loads(line)
            except json.JSONDecodeError:
                raise RuntimeError(f"Non-protocol output on stdout: {line[:200]!r}")
            if message.get("id") == expected_id:
                return message

    def call(request_id, tool):
        send({"jsonrpc": "2.0", "id": request_id, "method": "tools/call", "params": {"name": tool, "arguments": {}}})
        result = read(request_id)["result"]
        return json.loads("".join(item.get("text", "") for item in result["content"]))

    try:
        send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "phonedesk-smoke-test", "version": "1"}}})
        initialize = read(1)["result"]
        send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
        tools = {tool["name"] for tool in read(2)["result"]["tools"]}

        missing = EXPECTED_TOOLS - tools
        if missing:
            raise RuntimeError(f"Tools missing from the server: {sorted(missing)}")

        status = call(3, "session_status")
        if status.get("teamsConnected") is not False:
            raise RuntimeError(f"Unexpected session status: {status}")
        configuration = call(4, "configuration_get")
        if configuration["document"]["kind"] != "phonedesk.configuration":
            raise RuntimeError("configuration_get did not return a configuration document.")

        print(f"MCP server {initialize['serverInfo']['name']} {initialize['serverInfo']['version']}: "
              f"{len(tools)} tools, handshake and offline calls OK.")
        return 0
    except Exception as error:  # the smoke test reports any failure as a failed step
        print(f"MCP server smoke test failed: {error}", file=sys.stderr)
        return 1
    finally:
        try:
            server.stdin.close()
            server.wait(timeout=30)
        except Exception:
            server.kill()


if __name__ == "__main__":
    sys.exit(main())
