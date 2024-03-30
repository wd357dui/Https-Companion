using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Net.Security;
using System.Security.Authentication;
using HttpsCompanion;

Console.WriteLine("Hello, World!");

const int ProxyPort = 4887;
const string MyName = "HttpsCompanion";

using RSA rsa = RSA.Create(4096);
CertificateRequest request = new($"cn={MyName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));

using X509Certificate2 rootCACert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10));
Console.WriteLine("Root CA Certificate created successfully.");

using X509Store rootStore = new(StoreName.Root, StoreLocation.CurrentUser);
rootStore.Open(OpenFlags.ReadWrite);

X509Certificate2Collection existingCerts = rootStore.Certificates.Find(
	X509FindType.FindByIssuerDistinguishedName, $"cn={MyName}", false);
if (existingCerts.Count > 0)
{
	rootStore.RemoveRange(existingCerts);
}
rootStore.Add(rootCACert);

Dictionary<string, byte[]> domainCerts = [];

using CancellationTokenSource StopSource = new();
CancellationToken GetStopToken() => StopSource.Token;

using TcpListener listener = new(IPAddress.Any, ProxyPort);
listener.Start();
listener.BeginAcceptTcpClient(AcceptTcp, listener);

Console.WriteLine($"listening on port {ProxyPort}");

Console.ReadLine();

listener.Stop();
StopSource.Cancel();

existingCerts = rootStore.Certificates.Find(
	X509FindType.FindByIssuerDistinguishedName, $"cn={MyName}", false);
if (existingCerts.Count > 0)
{
	rootStore.RemoveRange(existingCerts);
}

void AcceptTcp(IAsyncResult asyncResult)
{
	if (asyncResult?.AsyncState is TcpListener listener)
	{
		try
		{
			TcpClient Remote = listener.EndAcceptTcpClient(asyncResult);
			new Thread(() => HandleTcpClient(Remote, HandlePreProxy)).Start();
			listener.BeginAcceptTcpClient(AcceptTcp, listener);
		}
		catch { }
	}
}

void HandleTcpClient(TcpClient Remote, Action<(string Method, string RawUrl, string ProtocolVersion,
	Dictionary<string, string> RequestHeaders, Stream RemoteStream,
	string Host, int? Port, Version Version,
	byte[] PreviousBytes)> Handler)
{
	using Stream RemoteStream = Remote.GetStream();
	Handlers.HandleTcp(RemoteStream, Handler);
	Remote.Close();
	Remote.Dispose();
}

void HandlePreProxy((string Method, string RawUrl, string ProtocolVersion,
	Dictionary<string, string> RequestHeaders, Stream RemoteStream,
	string Host, int? Port, Version Version,
	byte[] PreviousBytes) Args)
{
	if (Args.Version == HttpVersion.Version11)
	{
		// http 1.1
		switch (HttpMethod.Parse(Args.Method).Method)
		{
			case "CONNECT":
				HandlePreProxyConnect(Args.RemoteStream, Args.Host, Args.Port, Args.RequestHeaders);
				break;
			default:
				// just ignore non-HTTP-CONNECT requests
				Args.RemoteStream.Close();
				break;
		}
	}
	else
	{
		// not supported http protocol version
		Args.RemoteStream.Close();
	}
}

void HandlePreProxyConnect(Stream RemoteStream, string Host, int? Port, Dictionary<string, string> RequestHeaders)
{
	Dictionary<string, string> ResponseHeaders = [];
	if (ResponseHeaders.TryGetValue("Proxy-Connection", out string? ProxyKeepAlive) &&
		ProxyKeepAlive.Equals("keep-alive", StringComparison.InvariantCultureIgnoreCase))
	{
		ResponseHeaders["Proxy-Connection"] = "close";
		ResponseHeaders["Connection"] = "close";
	}
	if (ResponseHeaders.TryGetValue("Connection", out string? KeepAlive) &&
		KeepAlive.Equals("keep-alive", StringComparison.InvariantCultureIgnoreCase))
	{
		ResponseHeaders["Proxy-Connection"] = "close";
		ResponseHeaders["Connection"] = "close";
	}
	RemoteStream.Write(Handlers.EncodeHttpResponse(HttpVersion.Version11, HttpStatusCode.OK, ResponseHeaders));
	RemoteStream.Flush();
	HandlePreProxyData(RemoteStream, Host, Port);
}

void HandlePreProxyData(Stream RemoteStream, string Host, int? Port)
{
	bool Secure = false;
	if (Port == 443) { Secure = true; }
	else if (Port == 80) { Secure = false; }
	else if (Port is not null)
	{
		Secure = true;
	}
	else
	{
		// When the port number is left completely unspecified,
		// it is most likely 443 nowadays, unlike 80 a decade ago.
		Port = 443;
		Secure = true;
	}

	using X509Certificate2 serverCert = PrepareServer(Host);
	using SslStream tunnel = new(RemoteStream);
	tunnel.AuthenticateAsServer(serverCert, false, SslProtocols.None, true);
	Handlers.HandleTcp(tunnel, Args =>
	{
		Args.Port = Port;
		if (Args.Version == HttpVersion.Version11 || Args.Version == HttpVersion.Version10)
		{
			// http 1.1
			Handlers.HandleProxy(Args, Secure, GetStopToken());
		}
		else
		{
			// not supported http protocol version
			Args.RemoteStream.Close();
		}
	});
}

X509Certificate2 PrepareServer(string Host)
{
	lock (domainCerts)
	{
		X509Certificate2? cert = null;
		if (domainCerts.TryGetValue(Host, out byte[]? certBytes))
		{
			cert = new X509Certificate2(certBytes, "password", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
		}

		if (cert == null)
		{
			using RSA rsa = RSA.Create(2048);
			CertificateRequest request = new(
				$"cn={Host}",
				rsa,
				HashAlgorithmName.SHA256,
				RSASignaturePadding.Pkcs1);

			request.CertificateExtensions.Add(
				new X509BasicConstraintsExtension(false, false, 0, false));
			request.CertificateExtensions.Add(
				new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
			request.CertificateExtensions.Add(
				new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
			SubjectAlternativeNameBuilder sanBuilder = new();
			sanBuilder.AddDnsName(Host);
			request.CertificateExtensions.Add(sanBuilder.Build());
			request.CertificateExtensions.Add(
				new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

			DateTimeOffset now = DateTimeOffset.UtcNow;
			using X509Certificate2 serverCert = request.Create(rootCACert, now, now.AddYears(1), rootCACert.GetSerialNumber());

			string FirstExportCert = serverCert.ExportCertificatePem();
			string FirstExportKey = rsa.ExportRSAPrivateKeyPem();
			cert = X509Certificate2.CreateFromPem(FirstExportCert, FirstExportKey);

			byte[] SecondExport = cert.Export(X509ContentType.Pfx, "password");
			domainCerts[Host] = SecondExport;
			cert.Dispose();
			cert = new X509Certificate2(SecondExport, "password", X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
		}

		return cert;
	}
}