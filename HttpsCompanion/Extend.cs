using System.Security.Cryptography.X509Certificates;

namespace HttpsCompanion
{
	public static class Extend
	{
		public static bool IsValid(this X509Certificate2 cert)
		{
			try
			{
				return cert.Verify();
			}
			catch { return false; }
		}
		public static int? ExplicitPort(this Uri uri)
		{
			try
			{
				if (uri.Port is int Port)
				{
					if (uri.OriginalString.Contains($":{Port}")) return Port;
					else if (uri.OriginalString.StartsWith("http://"))
					{
						// sometimes "http://" or "https://" is included in the raw url
						// or host name. yes, some times.
						return 80;
					}
					else if (uri.OriginalString.StartsWith("https://"))
					{
						return 443;
					}
				}
			}
			catch (InvalidOperationException) { }
			return null;
		}
	}
}