"""Real loopback transport checks; fixtures contain no runtime credentials."""
import asyncio
import importlib.util
from pathlib import Path
import shutil
import ssl
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "files/gateway-relay.py"
spec = importlib.util.spec_from_file_location("relay", SOURCE)
relay = importlib.util.module_from_spec(spec)
spec.loader.exec_module(relay)


class RelayTransport(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        asyncio.get_running_loop().set_debug(False)
        self.servers, self.tasks = [], set()

    async def asyncTearDown(self):
        for server in self.servers:
            server.close()
            await server.wait_closed()
        for task in list(self.tasks):
            task.cancel()
        await asyncio.gather(*self.tasks, return_exceptions=True)

    def handler(self, action):
        def accept(reader, writer):
            task = asyncio.create_task(action(reader, writer))
            self.tasks.add(task)
            task.add_done_callback(self.tasks.discard)
        return accept

    async def server(self, action, **kwargs):
        server = await asyncio.start_server(self.handler(action), "127.0.0.1", 0, **kwargs)
        self.servers.append(server)
        return server.sockets[0].getsockname()[1]

    async def bridge(self, port):
        async def action(reader, writer):
            await relay.forward(reader, writer, "127.0.0.1", port)
        return await self.server(action)

    async def test_half_close_preserves_large_binary_response(self):
        payload = bytes(range(256)) * 4096
        async def echo(reader, writer):
            data = await reader.read()
            writer.write(data)
            await writer.drain()
            writer.close()
            await writer.wait_closed()
        port = await self.bridge(await self.server(echo))
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        writer.write(payload)
        await writer.drain()
        writer.write_eof()
        self.assertEqual(await asyncio.wait_for(reader.readexactly(len(payload)), 5), payload)
        self.assertEqual(await asyncio.wait_for(reader.read(), 5), b"")
        writer.close()
        await writer.wait_closed()

    async def test_mutual_tls_peer_certificates_survive_passthrough(self):
        openssl = shutil.which("openssl")
        self.assertIsNotNone(openssl, "openssl is required for the mutual TLS acceptance fixture")
        payload = bytes(range(256)) * 1024
        with tempfile.TemporaryDirectory(prefix="hr-relay-tls-") as directory:
            root = Path(directory)
            def run(*args):
                subprocess.run([openssl, *args], cwd=root, check=True,
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20)
            run("req", "-x509", "-nodes", "-newkey", "rsa:2048", "-days", "1", "-subj", "/CN=synthetic-relay-ca",
                "-keyout", "ca.key", "-out", "ca.crt")
            for role in ("server", "client"):
                run("req", "-new", "-nodes", "-newkey", "rsa:2048", "-subj", "/CN=" + role,
                    "-keyout", role + ".key", "-out", role + ".csr")
                (root / (role + ".ext")).write_text("subjectAltName=DNS:localhost\nextendedKeyUsage="
                    + ("serverAuth" if role == "server" else "clientAuth") + "\n")
                run("x509", "-req", "-in", role + ".csr", "-CA", "ca.crt", "-CAkey", "ca.key", "-CAcreateserial",
                    "-days", "1", "-extfile", role + ".ext", "-out", role + ".crt")
            server_tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_tls.load_cert_chain(root / "server.crt", root / "server.key")
            server_tls.load_verify_locations(root / "ca.crt")
            server_tls.verify_mode = ssl.CERT_REQUIRED
            client_tls = ssl.create_default_context(cafile=str(root / "ca.crt"))
            client_tls.load_cert_chain(root / "client.crt", root / "client.key")
            expected = {role: ssl.PEM_cert_to_DER_cert((root / (role + ".crt")).read_text())
                        for role in ("server", "client")}
            peer = asyncio.get_running_loop().create_future()
            async def echo(reader, writer):
                peer.set_result(writer.get_extra_info("ssl_object").getpeercert(binary_form=True))
                data = await reader.readexactly(len(payload))
                writer.write(data)
                await writer.drain()
                writer.close()
                await writer.wait_closed()
            port = await self.bridge(await self.server(echo, ssl=server_tls))
            reader, writer = await asyncio.wait_for(asyncio.open_connection(
                "127.0.0.1", port, ssl=client_tls, server_hostname="localhost"), 5)
            self.assertEqual(writer.get_extra_info("ssl_object").getpeercert(binary_form=True), expected["server"])
            writer.write(payload)
            await writer.drain()
            self.assertEqual(await asyncio.wait_for(reader.readexactly(len(payload)), 5), payload)
            self.assertEqual(await asyncio.wait_for(peer, 5), expected["client"])
            writer.close()
            await writer.wait_closed()

    async def test_unavailable_upstream_closes_without_hanging(self):
        closed = await asyncio.start_server(lambda r, w: w.close(), "127.0.0.1", 0)
        port = closed.sockets[0].getsockname()[1]
        closed.close()
        await closed.wait_closed()
        reader, writer = await asyncio.open_connection("127.0.0.1", await self.bridge(port))
        self.assertEqual(await asyncio.wait_for(reader.read(), 5), b"")
        writer.close()
        await writer.wait_closed()

    def test_unreviewed_destination_is_rejected(self):
        for host in ("127.0.0.1", "example.com", "other.other-namespace.svc.cluster.local"):
            result = subprocess.run([sys.executable, str(SOURCE), host], capture_output=True, timeout=5)
            self.assertNotEqual(result.returncode, 0)


if __name__ == "__main__":
    unittest.main()
