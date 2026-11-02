"""Exercise the snapshot CLI's acceptance boundary through a real HTTP socket."""
import http.server
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest


class SnapshotTests(unittest.TestCase):
    def test_declared_series_must_be_present_and_finite(self):
        response = {"series": []}

        class Prometheus(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                payload = json.dumps({"status": "success", "data": {"result": response["series"]}}).encode()
                self.send_response(200)
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *_):
                pass

        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Prometheus) as server:
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            try:
                with tempfile.TemporaryDirectory() as directory:
                    output = Path(directory) / "snapshot.json"
                    command = [sys.executable, str(Path(__file__).resolve().parents[2] / "evidence" / "performance-snapshot.py"),
                               "--prometheus", f"http://127.0.0.1:{server.server_port}", "--run", "contract-test",
                               "--output", str(output), "--queries", "consumer_redelivery_rate"]
                    for values, expected in (([], "FAIL"), ([{"value": [1, "NaN"]}], "FAIL"),
                                             ([{"value": [1, "+Inf"]}], "FAIL"),
                                             ([{"value": [1, "invalid"]}], "FAIL"),
                                             ([{"value": [1, "0"]}], "PASS")):
                        with self.subTest(values=values):
                            response["series"] = values
                            result = subprocess.run(command, capture_output=True, text=True)
                            self.assertEqual(json.loads(output.read_text())["verdict"], expected)
                            self.assertEqual(result.returncode == 0, expected == "PASS")
            finally:
                server.shutdown()
                thread.join()


if __name__ == "__main__":
    unittest.main()
