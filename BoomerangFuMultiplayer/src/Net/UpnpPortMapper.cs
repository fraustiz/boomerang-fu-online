using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace BoomerangFuMultiplayer.Net
{
    /// <summary>
    /// Opens the hosting port on the home router through UPnP IGD (SSDP discovery + SOAP), and
    /// asks the router for the public IP. No third-party service, nothing to install. Runs on a
    /// worker thread; results are read on the main thread through <see cref="Result"/>.
    /// </summary>
    public sealed class UpnpPortMapper
    {
        public sealed class Outcome
        {
            public bool Mapped;
            public string PublicIp;
            public string LocalIp;
            public string Router;
            public string Error;
        }

        static readonly string[] SearchTargets =
        {
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
        };

        const string Description = "Boomerang Fu Online";

        readonly int _port;
        volatile Outcome _result;
        string _controlUrl, _serviceType, _localIp;

        public Outcome Result => _result;
        public bool Done => _result != null;

        public UpnpPortMapper(int port) { _port = port; }

        public void Start()
        {
            var th = new Thread(Run) { IsBackground = true, Name = "BFUMP-UPnP" };
            th.Start();
        }

        void Run()
        {
            var o = new Outcome();
            try
            {
                var location = Discover();
                if (location == null) { o.Error = "aucune box UPnP trouvée"; _result = o; return; }
                o.Router = new Uri(location).Host;
                if (!FindControlUrl(location)) { o.Error = "la box ne propose pas l'ouverture de ports UPnP"; _result = o; return; }
                _localIp = LocalIpTowards(o.Router);
                o.LocalIp = _localIp;
                try { o.PublicIp = Soap("GetExternalIPAddress", "", "NewExternalIPAddress"); } catch { }

                Soap("AddPortMapping",
                    "<NewRemoteHost></NewRemoteHost>" +
                    $"<NewExternalPort>{_port}</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>" +
                    $"<NewInternalPort>{_port}</NewInternalPort>" +
                    $"<NewInternalClient>{_localIp}</NewInternalClient>" +
                    "<NewEnabled>1</NewEnabled>" +
                    $"<NewPortMappingDescription>{Description}</NewPortMappingDescription>" +
                    "<NewLeaseDuration>0</NewLeaseDuration>", null);
                o.Mapped = true;
            }
            catch (Exception e) { o.Error = e.Message; }
            _result = o;
        }

        /// <summary>Remove the mapping (best effort, synchronous with a short timeout).</summary>
        public void Remove()
        {
            if (_controlUrl == null || _result == null || !_result.Mapped) return;
            try
            {
                Soap("DeletePortMapping",
                    "<NewRemoteHost></NewRemoteHost>" +
                    $"<NewExternalPort>{_port}</NewExternalPort>" +
                    "<NewProtocol>UDP</NewProtocol>", null, 1500);
            }
            catch { }
        }

        // ── SSDP ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// SSDP search sent from every local IPv4 interface: with several adapters (Hyper-V,
        /// VPNs…) the default route for multicast is often not the one leading to the box.
        /// </summary>
        static string Discover()
        {
            var sockets = new List<UdpClient>();
            try
            {
                foreach (var ip in LocalIPv4())
                {
                    try
                    {
                        var udp = new UdpClient(new IPEndPoint(ip, 0));
                        udp.Client.ReceiveTimeout = 200;
                        sockets.Add(udp);
                    }
                    catch { }
                }
                if (sockets.Count == 0) sockets.Add(new UdpClient { Client = { ReceiveTimeout = 200 } });

                var target = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                foreach (var udp in sockets)
                    foreach (var st in SearchTargets)
                    {
                        var req = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + st + "\r\n\r\n";
                        var bytes = Encoding.ASCII.GetBytes(req);
                        try { udp.Send(bytes, bytes.Length, target); } catch { }
                    }

                var deadline = DateTime.UtcNow.AddSeconds(4);
                string fallback = null;
                while (DateTime.UtcNow < deadline)
                {
                    foreach (var udp in sockets)
                    {
                        try
                        {
                            if (udp.Available == 0) continue;
                            var from = new IPEndPoint(IPAddress.Any, 0);
                            var text = Encoding.ASCII.GetString(udp.Receive(ref from));
                            var loc = Regex.Match(text, @"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
                            if (!loc.Success) continue;
                            bool gateway = Regex.IsMatch(text, "InternetGatewayDevice|WANIPConnection|WANPPPConnection", RegexOptions.IgnoreCase);
                            if (gateway) return loc.Groups[1].Value.Trim();
                            fallback ??= loc.Groups[1].Value.Trim();
                        }
                        catch (SocketException) { }
                    }
                    Thread.Sleep(30);
                }
                return null; // only answers from non-gateway devices: no IGD
            }
            finally
            {
                foreach (var s in sockets) try { s.Close(); } catch { }
            }
        }

        static IEnumerable<IPAddress> LocalIPv4()
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                System.Net.NetworkInformation.IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }
                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = a.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local
                    yield return a;
                }
            }
        }

        bool FindControlUrl(string location)
        {
            var req = (HttpWebRequest)WebRequest.Create(location);
            req.Timeout = 3000;
            string xml;
            using (var resp = req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream())) xml = sr.ReadToEnd();

            foreach (var type in new[] { "WANIPConnection:2", "WANIPConnection:1", "WANPPPConnection:1" })
            {
                var m = Regex.Match(xml,
                    @"<service>\s*<serviceType>(urn:schemas-upnp-org:service:" + Regex.Escape(type) + @")</serviceType>.*?<controlURL>([^<]+)</controlURL>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                _serviceType = m.Groups[1].Value;
                var baseMatch = Regex.Match(xml, @"<URLBase>([^<]+)</URLBase>", RegexOptions.IgnoreCase);
                var baseUri = baseMatch.Success ? new Uri(baseMatch.Groups[1].Value) : new Uri(location);
                _controlUrl = new Uri(baseUri, m.Groups[2].Value.Trim()).ToString();
                return true;
            }
            return false;
        }

        string Soap(string action, string args, string resultTag, int timeout = 4000)
        {
            var body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                $"<s:Body><u:{action} xmlns:u=\"{_serviceType}\">{args}</u:{action}></s:Body></s:Envelope>";
            var req = (HttpWebRequest)WebRequest.Create(_controlUrl);
            req.Method = "POST";
            req.Timeout = timeout;
            req.ContentType = "text/xml; charset=\"utf-8\"";
            req.Headers.Add("SOAPACTION", $"\"{_serviceType}#{action}\"");
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
            string text;
            try
            {
                using (var resp = req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream())) text = sr.ReadToEnd();
            }
            catch (WebException we) when (we.Response != null)
            {
                using (var sr = new StreamReader(we.Response.GetResponseStream())) text = sr.ReadToEnd();
                var err = Regex.Match(text, @"<errorDescription>([^<]+)</errorDescription>");
                throw new Exception($"{action} refusé par la box ({(err.Success ? err.Groups[1].Value : we.Message)})");
            }
            if (resultTag == null) return null;
            var m = Regex.Match(text, "<" + resultTag + ">([^<]*)</" + resultTag + ">");
            return m.Success ? m.Groups[1].Value : null;
        }

        static string LocalIpTowards(string host)
        {
            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect(host, 1900);
                return ((IPEndPoint)s.LocalEndPoint).Address.ToString();
            }
        }
    }
}
