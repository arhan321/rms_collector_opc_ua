# RMS mock-eight-robot import for Ignition 8.3 Script Console (Jython 2.7).
# This creates OPC tags under [default]RMS_SIM; it never writes to OPC or SQL.
# Existing tags are protected by abort-on-collision. Keep the collector running.
import re

OPC_SERVER = "RMS YMConnect"
PROVIDER = "[default]"
ROOT_FOLDER = "RMS_SIM"
EXPECTED_IDS = list(range(1, 9))
TAG_DEFINITIONS = [
    ('RobotId', 'Int4'),
    ('IpAddress', 'String'),
    ('Group', 'String'),
    ('Simulated', 'Boolean'),
    ('Connected', 'Boolean'),
    ('HasFreshTelemetry', 'Boolean'),
    ('ConnectionMessage', 'String'),
    ('Identity/RobotCode', 'String'),
    ('Identity/Name', 'String'),
    ('Identity/Model', 'String'),
    ('Identity/ControllerModel', 'String'),
    ('Identity/SerialNumber', 'String'),
    ('Identity/LineName', 'String'),
    ('Identity/StationName', 'String'),
    ('Identity/Json', 'String'),
    ('Identity/AxisCount', 'Int4'),
    ('Identity/ReferenceHome/S', 'Float8'),
    ('Identity/ReferenceHome/L', 'Float8'),
    ('Identity/ReferenceHome/U', 'Float8'),
    ('Identity/ReferenceHome/R', 'Float8'),
    ('Identity/ReferenceHome/B', 'Float8'),
    ('Identity/ReferenceHome/T', 'Float8'),
    ('Telemetry/ReadOk', 'Boolean'),
    ('Telemetry/SampledAtUtc', 'DateTime'),
    ('Status/ReadOk', 'Boolean'),
    ('Status/SampledAtUtc', 'DateTime'),
    ('Job/ReadOk', 'Boolean'),
    ('Job/SampledAtUtc', 'DateTime'),
    ('Operating/ReadOk', 'Boolean'),
    ('Operating/SampledAtUtc', 'DateTime'),
    ('Alarms/ReadOk', 'Boolean'),
    ('Alarms/SampledAtUtc', 'DateTime'),
    ('IO/ReadOk', 'Boolean'),
    ('IO/SampledAtUtc', 'DateTime'),
    ('System/ReadOk', 'Boolean'),
    ('System/SampledAtUtc', 'DateTime'),
    ('Simulation/ReadOk', 'Boolean'),
    ('Simulation/SampledAtUtc', 'DateTime'),
    ('Telemetry/SessionId', 'String'),
    ('Telemetry/Sequence', 'Int8'),
    ('Telemetry/SnapshotJson', 'String'),
    ('Telemetry/Torque/Unit', 'String'),
    ('Telemetry/Torque/ReadOk', 'Boolean'),
    ('Telemetry/Torque/StatusCode', 'Int4'),
    ('Telemetry/Torque/S', 'Float8'),
    ('Telemetry/Torque/L', 'Float8'),
    ('Telemetry/Torque/U', 'Float8'),
    ('Telemetry/Torque/R', 'Float8'),
    ('Telemetry/Torque/B', 'Float8'),
    ('Telemetry/Torque/T', 'Float8'),
    ('Telemetry/Pulse/Unit', 'String'),
    ('Telemetry/Pulse/ReadOk', 'Boolean'),
    ('Telemetry/Pulse/StatusCode', 'Int4'),
    ('Telemetry/Pulse/S', 'Float8'),
    ('Telemetry/Pulse/L', 'Float8'),
    ('Telemetry/Pulse/U', 'Float8'),
    ('Telemetry/Pulse/R', 'Float8'),
    ('Telemetry/Pulse/B', 'Float8'),
    ('Telemetry/Pulse/T', 'Float8'),
    ('Status/Running', 'Boolean'),
    ('Status/ServoOn', 'Boolean'),
    ('Status/Alarming', 'Boolean'),
    ('Status/Hold', 'Boolean'),
    ('Status/Error', 'Boolean'),
    ('Status/ControlMode', 'String'),
    ('Status/CycleMode', 'String'),
    ('Job/Name', 'String'),
    ('Job/Line', 'Int4'),
    ('Job/Step', 'Int4'),
    ('Job/SpeedOverridePercent', 'Float8'),
    ('Job/ReadAtUtc', 'DateTime'),
    ('Job/ReadStatus', 'String'),
    ('Job/Task', 'String'),
    ('Job/OemDiagnosisAvailable', 'Boolean'),
    ('Operating/ControllerOnTime/Seconds', 'Int8'),
    ('Operating/ControllerOnTime/Hours', 'Float8'),
    ('Operating/ControllerOnTime/ReadOk', 'Boolean'),
    ('Operating/ServoPowerOnTimeRobot/Seconds', 'Int8'),
    ('Operating/ServoPowerOnTimeRobot/Hours', 'Float8'),
    ('Operating/ServoPowerOnTimeRobot/ReadOk', 'Boolean'),
    ('Operating/PlayBackTimeRobot/Seconds', 'Int8'),
    ('Operating/PlayBackTimeRobot/Hours', 'Float8'),
    ('Operating/PlayBackTimeRobot/ReadOk', 'Boolean'),
    ('Operating/MovingTimeRobot/Seconds', 'Int8'),
    ('Operating/MovingTimeRobot/Hours', 'Float8'),
    ('Operating/MovingTimeRobot/ReadOk', 'Boolean'),
    ('Alarms/ActiveCount', 'Int4'),
    ('Alarms/ActiveJson', 'String'),
    ('Alarms/HistoryJson', 'String'),
    ('Alarms/HistoryReadOk', 'Boolean'),
    ('IO/Json', 'String'),
    ('IO/Byte10', 'Int4'),
    ('IO/Byte10010', 'Int4'),
    ('System/Json', 'String'),
    ('Simulation/Phase', 'String'),
    ('Simulation/HomeStatus', 'String'),
    ('Simulation/Json', 'String'),
    ('Simulation/CycleSecond', 'Float8'),
    ('Simulation/CycleIndex', 'Int8'),
    ('Simulation/CompletedDemoJobs', 'Int8'),
    ('Simulation/CommunicationAvailable', 'Boolean'),
]


def node_id(element):
    value = element.getNodeId()
    if hasattr(value, "getNodeId"):
        value = value.getNodeId()
    return str(value)


def discover_namespace():
    queue = [("", 0)]
    seen = set()
    while queue:
        current, depth = queue.pop(0)
        if current in seen:
            continue
        seen.add(current)
        for item in system.opc.browseServer(OPC_SERVER, current):
            item_id = node_id(item)
            display = str(item.getDisplayName())
            if display == "RMS" and item_id.endswith(";s=RMS"):
                match = re.search(r"(?:^|;)ns=(\d+)", item_id)
                if not match:
                    raise ValueError("Cannot determine RMS namespace from " + item_id)
                return int(match.group(1))
            if depth < 3 and display in ("Objects", "Root"):
                queue.append((item_id, depth + 1))
    raise ValueError("RMS root not found. Check OPC connection name and collector status.")


def add_tag(root, path, config):
    parent = root
    parts = path.split("/")
    for name in parts[:-1]:
        matches = [item for item in parent["tags"] if item["name"] == name]
        if matches:
            parent = matches[0]
        else:
            folder = {"name": name, "tagType": "Folder", "tags": []}
            parent["tags"].append(folder)
            parent = folder
    config["name"] = parts[-1]
    parent["tags"].append(config)


def install_rms_sim_tags():
    destination = PROVIDER + ROOT_FOLDER
    if system.tag.exists(destination):
        raise ValueError(destination + " already exists. Choose another ROOT_FOLDER to avoid overwriting existing tags.")
    namespace = discover_namespace()
    prefix = "ns=%d;s=RMS/Robots/" % namespace
    robot_nodes = system.opc.browseServer(OPC_SERVER, "ns=%d;s=RMS/Robots" % namespace)
    actual_ids = sorted(int(str(item.getDisplayName())) for item in robot_nodes if str(item.getDisplayName()).isdigit())
    if actual_ids != EXPECTED_IDS:
        raise ValueError("Expected robot IDs %s; collector exposes %s. Start collector.mock8.conf first." % (EXPECTED_IDS, actual_ids))
    simulated = system.opc.readValues(OPC_SERVER, [prefix + str(robot_id) + "/Simulated" for robot_id in EXPECTED_IDS])
    if len(simulated) != len(EXPECTED_IDS) or any(not value.quality.isGood() or value.value != True for value in simulated):
        raise ValueError("All robots must expose Simulated=true with readable metadata. No tags were created.")
    root = {"name": ROOT_FOLDER, "tagType": "Folder", "tags": []}
    count = 0
    for robot_id in EXPECTED_IDS:
        for relative_path, datatype in TAG_DEFINITIONS:
            add_tag(root, "Robots/%d/%s" % (robot_id, relative_path), {
                "tagType": "AtomicTag", "valueSource": "opc", "dataType": datatype,
                "opcServer": OPC_SERVER,
                "opcItemPath": prefix + str(robot_id) + "/" + relative_path,
                "historyEnabled": False,
                "tooltip": "SIMULATION ONLY - RMS virtual robot %d" % robot_id
            })
            count += 1
    results = system.tag.configure(PROVIDER, [root], "a")
    if not results or any(not quality.isGood() for quality in results):
        raise RuntimeError("Ignition rejected tag configuration: %s" % list(results))
    print("Created %d OPC tags for %d simulated robots under %s" % (count, len(EXPECTED_IDS), destination))
    print("Namespace index discovered from server: %d" % namespace)
    print("Mock measurements intentionally report UncertainSubstituteValue; communication-loss windows report Bad.")
    return count


if not globals().get("RMS_IMPORT_LIBRARY_MODE", False):
    install_rms_sim_tags()
