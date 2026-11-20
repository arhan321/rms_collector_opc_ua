using System.Net;
using Opc.Ua;
using Opc.Ua.Configuration;
using Opc.Ua.Server;

namespace RmsCollector;

internal sealed class OpcUaHost : IAsyncDisposable
{
    private readonly ApplicationInstance application;
    private readonly Timer watchdog;
    public RmsNodeManager Nodes { get; }

    private OpcUaHost(ApplicationInstance application, RmsNodeManager nodes)
    {
        this.application = application;
        Nodes = nodes;
        watchdog = new Timer(_ => nodes.Expire(DateTime.UtcNow), null, 500, 500);
    }

    public static async Task<OpcUaHost> StartAsync(OpcUaSettings settings, bool mock)
    {
        var telemetry = DefaultTelemetry.Create(_ => { });
        string Store(string child) => Path.Combine(settings.PkiDirectory, child);
        var config = new ApplicationConfiguration
        {
            ApplicationName = "RMS YMConnect OPC UA",
            ApplicationUri = $"urn:{Dns.GetHostName()}:ADM:RMS:Collector",
            ProductUri = "urn:adm:rms:collector",
            ApplicationType = ApplicationType.Server,
            TransportQuotas = new TransportQuotas { OperationTimeout = 15000, MaxStringLength = 1_048_576, MaxByteStringLength = 1_048_576 },
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = "Directory", StorePath = Store("own"), SubjectName = "CN=RMS YMConnect OPC UA, O=ADM"
                },
                TrustedPeerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Store("trusted") },
                TrustedIssuerCertificates = new CertificateTrustList { StoreType = "Directory", StorePath = Store("issuers") },
                RejectedCertificateStore = new CertificateTrustList { StoreType = "Directory", StorePath = Store("rejected") },
                AutoAcceptUntrustedCertificates = false,
                RejectSHA1SignedCertificates = true,
                MinimumCertificateKeySize = 2048,
                AddAppCertToTrustedStore = false,
                SendCertificateChain = true
            },
            ServerConfiguration = new ServerConfiguration
            {
                BaseAddresses = new StringCollection { settings.Endpoint.AbsoluteUri },
                SecurityPolicies = new ServerSecurityPolicyCollection
                {
                    new ServerSecurityPolicy { SecurityMode = MessageSecurityMode.SignAndEncrypt, SecurityPolicyUri = SecurityPolicies.Basic256Sha256 }
                },
                UserTokenPolicies = new UserTokenPolicyCollection { new UserTokenPolicy(UserTokenType.Anonymous) },
                MaxSessionCount = 20,
                MinSessionTimeout = 10000,
                MaxSessionTimeout = 120000,
                MaxSubscriptionCount = 50
            }
        };
        await config.ValidateAsync(ApplicationType.Server);
        config.CertificateValidator.CertificateValidation += (_, e) =>
            AppLog.Write($"[OPCUA CERT] Rejected peer: {e.Error.StatusCode}; subject={e.Certificate.Subject}; thumbprint={e.Certificate.Thumbprint}. Check the rejected certificate before trusting it.");
        var application = new ApplicationInstance(config, telemetry);
        if (!await application.CheckApplicationInstanceCertificatesAsync(true))
            throw new InvalidOperationException("Unable to create or load the OPC UA application certificate");
        var server = new RmsServer(settings.Robots, mock);
        try
        {
            await application.StartAsync(server);
            return new OpcUaHost(application, server.Nodes ?? throw new InvalidOperationException("No RMS OPC UA address space"));
        }
        catch { await application.StopAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        await watchdog.DisposeAsync();
        await application.StopAsync();
    }
}

internal sealed class RmsServer(CollectorConfig robots, bool mock) : StandardServer
{
    public RmsNodeManager? Nodes { get; private set; }

    protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
    {
        Nodes = new RmsNodeManager(server, configuration, robots, mock);
        return new MasterNodeManager(server, configuration, null, new INodeManager[] { Nodes });
    }
}
