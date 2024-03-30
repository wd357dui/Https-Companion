using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace HttpsCompanion
{
	public static class Handlers
	{
		public static void HandleTcp(Stream RemoteStream, Action<(string Method, string RawUrl, string ProtocolVersion,
			Dictionary<string, string> RequestHeaders, Stream RemoteStream,
			string Host, int? Port, Version Version,
			byte[] PreviousBytes)> Handler)
		{
			try
			{
				string HttpRequest = ReceiveHttpRequest(RemoteStream, out byte[] HttpRequestBytes);
				Dictionary<string, string> RequestHeaders = DecodeHttpRequest(HttpRequest,
					out string Method, out string RawUrl, out string ProtocolVersion,
					out Uri? RawUrlAsUri, out Uri? HostAsUri);
				string? Host = HostAsUri?.Host ?? RawUrlAsUri?.Host;
				int? Port = HostAsUri?.ExplicitPort() ?? RawUrlAsUri?.ExplicitPort();
				Version? Version = null;
				if (Host is null)
				{
					// no valid host name
					RemoteStream.Write(EncodeHttpResponse(HttpVersion.Version11, HttpStatusCode.BadRequest, new() {
				{ "Connection", "close" },
				{ "Content-Length", "0" },
			}));
					RemoteStream.Flush();
				}
				else if (!ProtocolVersion.StartsWith("HTTP/") ||
					!Version.TryParse(ProtocolVersion["HTTP/".Length..], out Version))
				{
					// cannot parse http protocol version
					RemoteStream.Write(EncodeHttpResponse(HttpVersion.Version11, HttpStatusCode.BadRequest, new() {
				{ "Connection", "close" },
				{ "Content-Length", "0" },
			}));
					RemoteStream.Flush();
				}
				else
				{
					Handler((Method, RawUrl, ProtocolVersion, RequestHeaders, RemoteStream, Host, Port, Version, HttpRequestBytes));
				}
			}
			catch (IOException)
			{
				// connection closed by user or server, no action required
			}
			catch (AggregateException ae) when (ae.InnerExceptions.LastOrDefault() is IOException)
			{
				// connection closed by user or server, no action required
			}
			catch (CryptographicException)
			{
				// probably because connection closed by user
				// or it may happen when application is exiting
			}
			catch (Exception e)
			{
				// unexpected exception
				Type ExceptionType = e.GetType();
				string ErrorMessage = e.Message;
				if (e.InnerException is Exception Inner)
				{
					Type InnerExceptionType = Inner.GetType();
					string InnerErrorMessage = Inner.Message;
				}
			}
			finally
			{
				RemoteStream.Close();
				RemoteStream.Dispose();
			}
		}

		public static string ReceiveHttpRequest(Stream RemoteStream, out byte[] HttpRequestBytes)
		{
			using MemoryStream Buffer = new();
			string Result = "";
			try
			{
				do
				{
					int NewByteCount = (Result.EndsWith('\r') || Result.EndsWith('\n')) ? 1 : 4;
					byte[] bytes = new byte[NewByteCount];
					int NumRead = RemoteStream.Read(bytes, 0, bytes.Length);
					Buffer.Write(bytes, 0, NumRead);
					Result = Encoding.UTF8.GetString(Buffer.GetBuffer(), 0, (int)Buffer.Length);
				} while (!Result.EndsWith("\r\n\r\n"));
			}
			catch { }
			HttpRequestBytes = Buffer.ToArray();
			return Result;
		}

		public static Dictionary<string, string> DecodeHttpRequest(string Request,
			out string Method, out string RawUrl, out string ProtocolVersion,
			out Uri? RawUrlAsUri, out Uri? HostAsUri)
		{
			char[] separator = ['\r', '\n'];
			string[] Lines = Request.Split(separator, StringSplitOptions.RemoveEmptyEntries);

			string[] Parts = Lines[0].Split(' ');
			Method = Parts.First();
			RawUrl = string.Concat(Parts[1..^1]);
			ProtocolVersion = Parts.Last();

			Dictionary<string, string> Headers = [];
			foreach (string Pair in Lines[1..])
			{
				int Index = Pair.IndexOf(": ");
				if (Index != -1 && Index != 0)
				{
					Headers.Add(Pair[..Index], Pair[(Index + ": ".Length)..]);
				}
			}

			Uri.TryCreate(RawUrl.StartsWith("http:") ? RawUrl : "http://" + RawUrl, UriKind.RelativeOrAbsolute, out RawUrlAsUri);

			if (!Headers.TryGetValue("Host", out string? Host))
			{
				if (RawUrlAsUri is not null && !string.IsNullOrEmpty(RawUrlAsUri.Host))
				{
					Host = RawUrlAsUri.Host;
				}
				else
				{
					Host = null;
				}
			}
			if (Host is not null)
			{
				Uri.TryCreate(Host.StartsWith("http:") ? Host : "http://" + Host, UriKind.RelativeOrAbsolute, out HostAsUri);
			}
			else
			{
				HostAsUri = null;
			}

			return Headers;
		}

		public static byte[] EncodeHttpResponse(Version ProtocolVersion, HttpStatusCode Status, Dictionary<string, string> ResponseHeaders)
		{
			string HeadersString = string.Concat(ResponseHeaders.Select((Header, Value) => $"{Header}: {Value}\r\n"));
			return Encoding.UTF8.GetBytes($"HTTP/{ProtocolVersion} {(int)Status} {Status}\r\n" +
				$"{HeadersString}\r\n");
		}

		public static void HandleProxy((string Method, string RawUrl, string ProtocolVersion,
			Dictionary<string, string> RequestHeaders, Stream RemoteStream,
			string Host, int? Port, Version Version,
			byte[] PreviousBytes) Args,
			bool Secure,
			CancellationToken StopToken)
		{
			try
			{
				using TcpClient Client = new()
				{
					SendTimeout = 10 * 1000,
					ReceiveTimeout = 10 * 1000,
				};
				Client.ConnectAsync(Args.Host, Args.Port!.Value, StopToken)
					.AsTask().Wait(StopToken);
				Stream ServerStream;
				if (Secure)
				{
					SslStream TlsStream = new(Client.GetStream(), false, null, null);
					TlsStream.AuthenticateAsClientAsync(Args.Host).Wait(StopToken);
					ServerStream = TlsStream;
				}
				else
				{
					ServerStream = Client.GetStream();
				}
				ServerStream.Write(Args.PreviousBytes);
				ServerStream.Flush();
				{
					// from here we have the opportunity to inspect the data
					// that is exchanging between the server and the client
				}
				Task Send = Args.RemoteStream.CopyToAsync(ServerStream, StopToken);
				Task Receive = ServerStream.CopyToAsync(Args.RemoteStream, StopToken);
				Task.WaitAll([Send, Receive], StopToken);
				ServerStream.Close();
			}
			catch (AggregateException ae) when (ae.InnerExceptions.LastOrDefault() is IOException) { }
			catch (OperationCanceledException) { }
			catch (Exception ex)
			{
				Type ExceptionType = ex.GetType();
				string _ = ex.Message;
			}
		}
	}
}
