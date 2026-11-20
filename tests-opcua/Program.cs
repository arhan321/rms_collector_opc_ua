using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using RmsCollector;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
        Console.WriteLine("PASS " + message);
    }

    public static async Task<int> Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "rms-opcua-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string robotConfig = Path.Combine(temp, "robots.conf");
            File.WriteAllText(robotConfig, "telemetry_ms=1000\noperating_every_s=5\nalarms_every_s=5\nextras_every_s=5\njob_monitor_every_sample=1\nrobot=1,127.0.0.1,R1,10;10010\nrobot=2,127.0.0.2,R1,10\n");
            var robots = CollectorConfig.Load(robotConfig);
            // Reserve an ephemeral loopback port; the server binds immediately afterwards.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            string url = $"opc.tcp://localhost:{port}/RMS/Collector";
            var settings = new OpcUaSettings(robots, new Uri(url), Path.Combine(temp, "server"));
            var telemetry = DefaultTelemetry.Create(_ => { });
            var clientConfig = new ApplicationConfiguration
            {
                ApplicationName = "RMS OPC UA integration test",
                ApplicationUri = $"urn:{Dns.GetHostName()}:RMS:OpcUaTest",
                ApplicationType = ApplicationType.Client,
                TransportQuotas = new TransportQuotas { OperationTimeout = 10000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60000 },
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier { StoreType = "Directory", StorePath = Path.Combine(temp, "client", "own"), SubjectName = "CN=RMS OPC UA integration test" },
                    TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(temp, "client", "trusted") },
                    TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(temp, "client", "issuers") },
                    RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Path.Combine(temp, "client", "rejected") },
                    AutoAcceptUntrustedCertificates = false,
                    AddAppCertToTrustedStore = false,
                    MinimumCertificateKeySize = 2048
                }
            };
            await clientConfig.ValidateAsync(ApplicationType.Client);
            var client = new ApplicationInstance(clientConfig, telemetry);
            Check(await client.CheckApplicationInstanceCertificatesAsync(true), "client certificate created");

            // Trust the server first, and verify that the server still rejects an unknown client.
            await using (var first = await OpcUaHost.StartAsync(settings, true))
            {
                Trust(Path.Combine(temp, "server", "own", "certs"), Path.Combine(temp, "client", "trusted", "certs"));
                await clientConfig.CertificateValidator.UpdateAsync(clientConfig.SecurityConfiguration);
                var secure = await CoreClientUtils.SelectEndpointAsync(clientConfig, url, true, telemetry, CancellationToken.None)
                    ?? throw new InvalidOperationException("No endpoint for trust test");
                bool rejected = false;
                try
                {
                    using var untrusted = await new DefaultSessionFactory(telemetry).CreateAsync(clientConfig,
                        new ConfiguredEndpoint(null, secure, EndpointConfiguration.Create(clientConfig)), false,
                        "Untrusted client", 60000, new UserIdentity(new AnonymousIdentityToken()), null, CancellationToken.None);
                    await untrusted.CloseAsync(CancellationToken.None);
                }
                catch (ServiceResultException) { rejected = true; }
                Check(rejected && Directory.GetFiles(Path.Combine(temp, "server", "rejected", "certs"), "*.der").Length > 0,
                    "unknown OPC client is rejected until its public certificate is trusted");
            }
            Trust(Path.Combine(temp, "client", "own", "certs"), Path.Combine(temp, "server", "trusted", "certs"));
            await clientConfig.CertificateValidator.UpdateAsync(clientConfig.SecurityConfiguration);
            await using var host = await OpcUaHost.StartAsync(settings, true);

            var endpoint = await CoreClientUtils.SelectEndpointAsync(clientConfig, url, true, telemetry, CancellationToken.None)
                ?? throw new InvalidOperationException("No secure endpoint");
            Check(endpoint.SecurityMode == MessageSecurityMode.SignAndEncrypt && endpoint.SecurityPolicyUri == SecurityPolicies.Basic256Sha256, "only the encrypted endpoint is selected");
            using var session = await new DefaultSessionFactory(telemetry).CreateAsync(clientConfig,
                new ConfiguredEndpoint(null, endpoint, EndpointConfiguration.Create(clientConfig)), false,
                "RMS tests", 60000, new UserIdentity(new AnonymousIdentityToken()), null, CancellationToken.None);
            Check(session.Connected, "secure OPC UA session with mutual certificate trust");
            int namespaceIndex = session.NamespaceUris.GetIndex(RmsNodeManager.NamespaceUri);
            Check(namespaceIndex > 0 && namespaceIndex < ushort.MaxValue, "stable RMS namespace is discoverable");
            ushort ns = checked((ushort)namespaceIndex);
            NodeId Id(string path, int robot = 1) => new($"RMS/Robots/{robot}/{path}", ns);
            async Task<DataValue> Read(string path, int robot = 1)
            {
                var response = await session.ReadAsync(null, 0, TimestampsToReturn.Both,
                    new ReadValueIdCollection { new() { NodeId = Id(path, robot), AttributeId = Attributes.Value } }, CancellationToken.None);
                return response.Results[0];
            }
            Check((await Read("Telemetry/Torque/S")).StatusCode == StatusCodes.BadWaitingForInitialData, "no zero-valued data before the first sample");
            Check((bool)(await Read("Simulated")).Value, "simulation is explicitly identified");

            var browse = await session.BrowseAsync(null, null, 0, new BrowseDescriptionCollection
            {
                new() { NodeId = new NodeId("RMS/Robots", ns), BrowseDirection = BrowseDirection.Forward,
                    ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences, IncludeSubtypes = true, ResultMask = (uint)BrowseResultMask.All }
            }, CancellationToken.None);
            Check(browse.Results[0].References.Count == 2, "two robots can be browsed separately");
            using var reader = new MockReader(robots.Devices[0], true);
            reader.Connect();
            var sample = reader.Collect(1, true, true, true);
            DateTime sampled = DateTime.UtcNow;
            host.Nodes.Publish(robots.Devices[0], sample, "test-session", 1, sampled, true, true, true);
            var torque = await Read("Telemetry/Torque/S");
            Check(torque.StatusCode == StatusCodes.UncertainSubstituteValue && Math.Abs((double)torque.Value - sample.Torques[0]) < 1e-8, "axis value is correct and mock quality is Uncertain");
            Check(Math.Abs((torque.SourceTimestamp - sampled).TotalMilliseconds) < 1, "OPC source timestamp preserves collector sample time");
            Check((string)(await Read("Job/Name")).Value == "MOCK_JOB", "job identity is available as an OPC tag");
            Check((long)(await Read("Operating/MovingTimeRobot/Seconds")).Value == 81 * 3600L + 5, "operating counter preserves seconds");
            Check((int)(await Read("IO/Byte10")).Value == 11, "I/O byte is mapped without changing its value");
            Check((int)(await Read("Alarms/ActiveCount")).Value == 0 && (await Read("Alarms/ActiveCount")).StatusCode == StatusCodes.UncertainSubstituteValue, "successful empty alarm result is distinguished from read failure");
            using (var json = JsonDocument.Parse((string)(await Read("Telemetry/SnapshotJson")).Value))
                Check(json.RootElement.GetProperty("sequence").GetInt64() == 1 && json.RootElement.GetProperty("raw_payload").GetProperty("simulated").GetBoolean(), "JSON snapshot carries identity, sequence and simulation metadata");
            Check((await Read("Telemetry/Torque/S", 2)).StatusCode == StatusCodes.BadWaitingForInitialData, "robot 1 cannot populate robot 2");

            var write = await session.WriteAsync(null, new WriteValueCollection
            {
                new() { NodeId = Id("Telemetry/Torque/S"), AttributeId = Attributes.Value, Value = new DataValue(999d) }
            }, CancellationToken.None);
            Check(StatusCode.IsBad(write.Results[0]) && (double)(await Read("Telemetry/Torque/S")).Value == sample.Torques[0], "OPC writes are rejected and cannot command the robot");

            var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 100, KeepAliveCount = 10, LifetimeCount = 100 };
            var item = new MonitoredItem(subscription.DefaultItem) { StartNodeId = Id("Telemetry/Sequence"), AttributeId = Attributes.Value, SamplingInterval = 500, QueueSize = 10, DiscardOldest = true };
            item.Notification += (_, _) => { foreach (var value in item.DequeueValues()) if (value.Value is long n && n == 2) changed.TrySetResult(true); };
            subscription.AddItem(item);
            session.AddSubscription(subscription);
            await subscription.CreateAsync(CancellationToken.None);
            host.Nodes.Publish(robots.Devices[0], reader.Collect(2, false, false, false), "test-session", 2, DateTime.UtcNow, false, false, false);
            Check(await changed.Task.WaitAsync(TimeSpan.FromSeconds(6)), "live OPC subscription receives the next sample");
            await subscription.DeleteAsync(true, CancellationToken.None);

            host.Nodes.Publish(robots.Devices[0], new RobotSample(), "test-session", 3, DateTime.UtcNow, true, true, true);
            Check(StatusCode.IsBad((await Read("Telemetry/Torque/S")).StatusCode) && (await Read("Telemetry/Torque/S")).Value is null, "failed reads publish Bad quality and no fake zero");
            Check(StatusCode.IsBad((await Read("Alarms/ActiveCount")).StatusCode), "failed alarm read is not reported as zero alarms");
            var malformed = reader.Collect(3, false, false, false);
            malformed.Torques[0] = double.NaN;
            host.Nodes.Publish(robots.Devices[0], malformed, "test-session", 3, DateTime.UtcNow, false, false, false);
            Check(StatusCode.IsBad((await Read("Telemetry/Torque/S")).StatusCode) && (await Read("Telemetry/SnapshotJson")).StatusCode == StatusCodes.BadDecodingError,
                "non-finite SDK values cannot be published as valid torque or JSON");
            host.Nodes.Publish(robots.Devices[0], reader.Collect(4, true, true, true), "test-session", 4, sampled = DateTime.UtcNow, true, true, true);
            host.Nodes.Expire(sampled.AddSeconds(60));
            var stale = await Read("Telemetry/Torque/S");
            Check(StatusCode.IsBad(stale.StatusCode) && Math.Abs((stale.SourceTimestamp - sampled).TotalMilliseconds) < 1, "stale values are Bad without advancing their source timestamp");
            Check(!(bool)(await Read("HasFreshTelemetry")).Value, "stale connection is visible to Ignition");
            host.Nodes.Publish(robots.Devices[0], reader.Collect(5, true, true, true), "test-session", 5, DateTime.UtcNow, true, true, true);
            Check((await Read("Telemetry/Torque/S")).StatusCode == StatusCodes.UncertainSubstituteValue && (bool)(await Read("HasFreshTelemetry")).Value, "fresh data recovers after stale detection");
            host.Nodes.Disconnect(1, "test disconnect");
            Check(!(bool)(await Read("Connected")).Value && StatusCode.IsBad((await Read("Job/Name")).StatusCode), "disconnect invalidates robot data");
            await session.CloseAsync(CancellationToken.None);
            Console.WriteLine($"SUCCESS: {checks} OPC UA integration checks");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }
    }

    private static void Trust(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*.der")) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
    }
}
