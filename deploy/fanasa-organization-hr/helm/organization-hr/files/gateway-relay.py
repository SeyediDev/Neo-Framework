#!/usr/bin/env python3
"""Bounded TCP passthrough; original Gateway and HR API negotiate TLS."""
import asyncio
import re
import sys

PORT = 5193
MAX_CONNECTIONS = 32
IDLE_SECONDS = 35
CONNECTION_SECONDS = 120

async def copy_stream(reader, writer):
    while True:
        data = await asyncio.wait_for(reader.read(65536), IDLE_SECONDS)
        if not data:
            if writer.can_write_eof():
                writer.write_eof()
            return
        writer.write(data)
        await asyncio.wait_for(writer.drain(), IDLE_SECONDS)

async def forward(reader, writer, host, port=PORT):
    upstream = None
    tasks = []
    transfer = None
    try:
        upstream_reader, upstream = await asyncio.wait_for(
            asyncio.open_connection(host, port, limit=65536), 5)
        tasks = [asyncio.create_task(copy_stream(reader, upstream)),
                 asyncio.create_task(copy_stream(upstream_reader, writer))]
        transfer = asyncio.gather(*tasks)
        await asyncio.wait_for(transfer, CONNECTION_SECONDS)
    except (OSError, TimeoutError, asyncio.IncompleteReadError):
        pass
    finally:
        for task in tasks:
            task.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        if transfer is not None:
            await asyncio.gather(transfer, return_exceptions=True)
        for stream in (upstream, writer):
            if stream is not None:
                stream.close()
                try:
                    await asyncio.wait_for(stream.wait_closed(), 2)
                except (OSError, TimeoutError):
                    pass

async def serve(host):
    active = set()
    def accept(reader, writer):
        if len(active) >= MAX_CONNECTIONS:
            writer.close()
            return
        task = asyncio.create_task(forward(reader, writer, host))
        active.add(task)
        task.add_done_callback(active.discard)
    server = await asyncio.start_server(accept, '0.0.0.0', PORT, limit=65536, backlog=32)
    async with server:
        await server.serve_forever()

if __name__ == '__main__':
    if len(sys.argv) != 2 or not re.fullmatch(
            r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.fanasa-organization-hr-development\.svc\.cluster\.local', sys.argv[1]):
        raise SystemExit('A dedicated HR ClusterIP service name is required')
    asyncio.run(serve(sys.argv[1]))
