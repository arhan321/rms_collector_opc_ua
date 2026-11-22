"""Contract tests with fake Ignition APIs; these do not claim a live Gateway test."""
import contextlib
import io
import json
from pathlib import Path
from types import SimpleNamespace
import unittest

HERE = Path(__file__).parent


class Quality:
    def __init__(self, good=True):
        self.good = good

    def isGood(self):
        return self.good


class Node:
    def __init__(self, name, item):
        self.name, self.item = name, item

    def getDisplayName(self):
        return self.name

    def getNodeId(self):
        return self.item


class FakeIgnition:
    def __init__(self, ids=range(1, 9), simulated=True, collision=False, good=True):
        self.ids = list(ids)
        self.simulated = simulated
        self.collision = collision
        self.good = good
        self.configured = None
        self.tag = SimpleNamespace(exists=lambda _: collision, configure=self.configure)
        self.opc = SimpleNamespace(browseServer=self.browse, readValues=self.read)

    def browse(self, server, node):
        assert server == "RMS YMConnect"
        if node == "":
            return [Node("Objects", "ns=0;i=85")]
        if node == "ns=0;i=85":
            return [Node("RMS", "ns=7;s=RMS")]
        if node == "ns=7;s=RMS/Robots":
            return [Node(str(robot), "ns=7;s=RMS/Robots/" + str(robot)) for robot in self.ids]
        raise AssertionError("Unexpected browse node: " + node)

    def read(self, server, paths):
        assert all(path.startswith("ns=7;s=RMS/Robots/") and path.endswith("/Simulated") for path in paths)
        return [SimpleNamespace(value=self.simulated, quality=Quality(self.good)) for _ in paths]

    def configure(self, provider, tags, policy):
        assert provider == "[default]" and policy == "a"
        self.configured = tags
        return [Quality()]


class ImportTests(unittest.TestCase):
    def load(self, system):
        namespace = {"RMS_IMPORT_LIBRARY_MODE": True, "system": system}
        source = (HERE / "import_mock8_tags.py").read_text()
        exec(compile(source, "import_mock8_tags.py", "exec"), namespace)
        return namespace

    def test_namespace_and_all_robot_paths(self):
        system = FakeIgnition()
        api = self.load(system)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(api["install_rms_sim_tags"](), 808)
        atomic = []

        def walk(tag):
            if tag["tagType"] == "AtomicTag":
                atomic.append(tag)
            for child in tag.get("tags", []):
                walk(child)

        walk(system.configured[0])
        self.assertEqual(len(atomic), 808)
        self.assertEqual(len({tag["opcItemPath"] for tag in atomic}), 808)
        self.assertTrue(all(tag["opcItemPath"].startswith("ns=7;s=RMS/Robots/") for tag in atomic))
        self.assertTrue(all(tag["valueSource"] == "opc" and not tag["historyEnabled"] for tag in atomic))
        self.assertEqual({tag["opcItemPath"].split("/")[2] for tag in atomic}, set(map(str, range(1, 9))))
        manifest = json.loads((HERE / "tag-manifest.json").read_text())
        self.assertEqual(set(api["TAG_DEFINITIONS"]), {(tag["path"], tag["dataType"]) for tag in manifest["tags"]})

    def test_blocks_wrong_inventory(self):
        system = FakeIgnition(ids=[1])
        with self.assertRaisesRegex(ValueError, "Expected robot IDs"):
            self.load(system)["install_rms_sim_tags"]()
        self.assertIsNone(system.configured)

    def test_blocks_real_robots(self):
        system = FakeIgnition(simulated=False)
        with self.assertRaisesRegex(ValueError, "Simulated=true"):
            self.load(system)["install_rms_sim_tags"]()
        self.assertIsNone(system.configured)

    def test_blocks_unreadable_metadata(self):
        system = FakeIgnition(good=False)
        with self.assertRaisesRegex(ValueError, "Simulated=true"):
            self.load(system)["install_rms_sim_tags"]()
        self.assertIsNone(system.configured)

    def test_existing_tags_are_not_overwritten(self):
        system = FakeIgnition(collision=True)
        with self.assertRaisesRegex(ValueError, "already exists"):
            self.load(system)["install_rms_sim_tags"]()
        self.assertIsNone(system.configured)


if __name__ == "__main__":
    unittest.main()
