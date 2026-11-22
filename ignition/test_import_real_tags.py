"""Contract checks for the real-only importer; no live Gateway is modified."""
import contextlib
import io
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from test_import_mock8_tags import Node, Quality

HERE = Path(__file__).parent


class RealGateway:
    def __init__(self, ids=(1, 12), simulated=False, good=True, collision=False):
        self.ids = list(ids)
        self.simulated = simulated
        self.good = good
        self.configured = None
        self.opc = SimpleNamespace(browseServer=self.browse, readValues=self.read)
        self.tag = SimpleNamespace(exists=lambda _: collision, configure=self.configure)

    def browse(self, server, node):
        assert server == "RMS YMConnect"
        if node == "":
            return [Node("Objects", "ns=0;i=85")]
        if node == "ns=0;i=85":
            return [Node("RMS", "ns=9;s=RMS")]
        if node == "ns=9;s=RMS/Robots":
            return [Node(str(robot), "ns=9;s=RMS/Robots/" + str(robot)) for robot in self.ids]
        if node.endswith("/IO"):
            names = ["ReadOk", "SampledAtUtc", "Json"] + (["Byte10", "Byte10010"] if "/1/" in node else ["Byte25"])
            return [Node(name, node + "/" + name) for name in names]
        raise AssertionError(node)

    def read(self, server, paths):
        assert all(path.startswith("ns=9;s=RMS/Robots/") and path.endswith("/Simulated") for path in paths)
        return [SimpleNamespace(value=self.simulated, quality=Quality(self.good)) for _ in paths]

    def configure(self, provider, tags, policy):
        assert provider == "[default]" and policy == "a"
        self.configured = tags
        return [Quality()]


class RealImportTests(unittest.TestCase):
    def load(self, gateway):
        namespace = {"RMS_IMPORT_LIBRARY_MODE": True, "system": gateway}
        exec(compile((HERE / "import_real_tags.py").read_text(), "import_real_tags.py", "exec"), namespace)
        return namespace

    def test_dynamic_ids_namespace_io_and_core_contract(self):
        gateway = RealGateway()
        api = self.load(gateway)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(api["install_rms_real_tags"](), 153)
        atomic = []

        def walk(tag):
            if tag["tagType"] == "AtomicTag":
                atomic.append(tag)
            for child in tag.get("tags", []):
                walk(child)

        walk(gateway.configured[0])
        paths = {tag["opcItemPath"] for tag in atomic}
        self.assertEqual(len(paths), 153)
        self.assertTrue(all(path.startswith("ns=9;s=RMS/Robots/") for path in paths))
        self.assertIn("ns=9;s=RMS/Robots/12/IO/Byte25", paths)
        self.assertNotIn("ns=9;s=RMS/Robots/12/IO/Byte10", paths)
        self.assertTrue(all("/Identity/" not in path and "/Simulation/" not in path for path in paths))
        self.assertTrue(all(not tag["historyEnabled"] and tag["valueSource"] == "opc" for tag in atomic))
        manifest = json.loads((HERE / "tag-manifest.json").read_text())
        expected = {(tag["path"], tag["dataType"]) for tag in manifest["tags"]
                    if not tag["path"].startswith(("Identity/", "Simulation/", "IO/Byte"))}
        self.assertEqual(set(api["CORE_TAG_DEFINITIONS"]), expected)

    def test_rejects_mock_or_unreadable_metadata(self):
        for settings in ({"simulated": True}, {"good": False}):
            gateway = RealGateway(**settings)
            with self.assertRaisesRegex(ValueError, "Simulated=false"):
                self.load(gateway)["install_rms_real_tags"]()
            self.assertIsNone(gateway.configured)

    def test_rejects_empty_duplicate_or_excess_inventory(self):
        for ids in ([], [1, 1], list(range(1, 10))):
            gateway = RealGateway(ids=ids)
            with self.assertRaisesRegex(ValueError, "1-8 unique"):
                self.load(gateway)["install_rms_real_tags"]()
            self.assertIsNone(gateway.configured)

    def test_existing_folder_is_preserved(self):
        gateway = RealGateway(collision=True)
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.load(gateway)["install_rms_real_tags"]()
        self.assertIsNone(gateway.configured)


if __name__ == "__main__":
    unittest.main()
