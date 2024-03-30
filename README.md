# Https Companion

This program allows you to "take control" of your own machine's HTTPS connections. ~~It's similar to Wireshark~~... **I JUST REALIZED THAT THE SOFTWARE I WAS USING FROM MANY YEARS AGO, [Fiddler](https://www.telerik.com/fiddler), WHICH I'VE SPENT THIS WEEK WRITING THIS PROJECT TO REPLACE, HAS ALREADY ADDED HTTPS SUPPORT, PRESUMABLY A LONG TIME AGO! I FEEL STUPID NOW AND I'M PUTTING THIS PROJECT ON HOLD (unless someone asks me to resume it)**
~~It functions on a higher level in the **application layer** instead of the **transport layer**, meaning it doesn't manage raw TCP data packets or TLS handshake packets, and only cares about the data that are transferred (including HTTP headers); unlike Wireshark, it only focuses on HTTP & HTTPS connections, but in addition to data inspection, it also adds the possibility for you to **manipulate the request & response data**.~~

# Major use cases

 - **~~Data inspection~~**

Just... go and use [Fiddler](https://www.telerik.com/fiddler)... I'm going to stay here and calm myself...

 - **Resumable download + multi-thread download acceleration for mainland China**

**Background**: People in the rest of the world may not realize this, but **"global" CDNs don't usually include China** due to the law. Any server within mainland China must obtain an [ICP license](https://wikipedia.org/wiki/ICP_license) (per website) to provide services legally.
This lack of CDN support caused headaches for some tech nerds/visual artists/developers in mainland China, especially those who are using Python applications that need to download large packages like AI models, which are huge.
Things like Python-based AI programs (or any other average software really) mostly don't support resumable downloads (they either can't, don't know how, or didn't believe they needed it, or just don't care), and since their CDNs are outside China, it's not uncommon that someone **waited hours, only to find out that their download connection was lost and they had to start over again**.

**Analysis**: It's not that they had bad internet (on the contrary, they may be using... for example, 500Mbps ChinaNet fiber), it's that each single international TCP connection tends to get QoS-ed somewhere (it could be happening on the MAN or even nationwide WAN level, which is definitely beyond the user's or even ISP's control), making it comparably slow, and by extension, unreliable. **With multi-thread downloading, however, you can easily achieve full bandwidth speed**. That's right, **the international speed limit mentioned above is not per cable, it's per TCP connection**, and one can open an unlimited number of TCP connections.

**Solution**: To solve resumable download support with this program, for each large file, we can store already downloaded data on the disk, acting as a persistent intermediate buffer (or you may call it cache), and for subsequent requests on the same resource, we just reply with data from that buffer.
To solve multi-thread download acceleration, we can just use multiple [TcpClient](https://learn.microsoft.com/dotnet/api/system.net.sockets.tcpclient) or [HttpClient](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient) at the same time, and request for different [Range](https://developer.mozilla.org/en-US/docs/Web/HTTP/Range_requests) of the resource from the real server. But what if the real server doesn't support [Range](https://developer.mozilla.org/en-US/docs/Web/HTTP/Range_requests)? In that case, you may not be completely screwed, this is the Internet after all, and there is a chance that the large file you need is already mirrored somewhere else on the Internet, where you can download reliably. After you've downloaded from there, you can put that in the buffer to reply to the client.

> [!NOTE]
> The solution theory is here, but I haven't implemented this feature yet,
> since I've put this project on hold I may not going to.
> This is already doable with [Fiddler](https://www.telerik.com/fiddler) anyway. (may not be convenient but do-able)
