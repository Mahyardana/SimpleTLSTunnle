﻿using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Diagnostics;
using Newtonsoft.Json;
using SimpleTLSTunnelClient;
using System.Security.Cryptography;
using System.Reflection.Metadata.Ecma335;
using System.Collections.Concurrent;
using System.Drawing.Drawing2D;

var firsttunnelsinit = true;
object tlock = new object();
ulong lastSessionID = 0;
var stableTunnelsCount = 0;
var maxStableTunnelsCount = 16;
var maxping = 0l;
ConcurrentQueue<TunnelSession> senderqueue = new ConcurrentQueue<TunnelSession>();
Dictionary<ulong, Dictionary<ulong, Packet>> responsesDict = new Dictionary<ulong, Dictionary<ulong, Packet>>();
Dictionary<ulong, Dictionary<ulong, Packet>> acksDict = new Dictionary<ulong, Dictionary<ulong, Packet>>();
TTunnelClientConfig config = null;
if (!File.Exists("config.json"))
{
    config = new TTunnelClientConfig();
    File.WriteAllText("config.json", JsonConvert.SerializeObject(config));
}
else
{
    config = JsonConvert.DeserializeObject<TTunnelClientConfig>(File.ReadAllText("config.json"));
    maxStableTunnelsCount = config.stable_tunnels;
}
//var encrypted=Encrypt(Encoding.ASCII.GetBytes("fuckme"));
//var decrypted=Decrypt(encrypted);
//var dectext = Encoding.ASCII.GetString(decrypted);
var cert = new X509Certificate2("cert.crt");
bool userCertificateValidationCallback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors)
{
    //var servercert = certificate as X509Certificate2;
    //if (certificate.GetSerialNumberString() == cert.GetSerialNumberString())
    //    return true;
    //else
    //    return false;
    return true;
}
void StableTunnelHandler()
{
    try
    {
        var tunnelid = 0;
        lock (tlock)
        {
            tunnelid = stableTunnelsCount;
            if (!firsttunnelsinit)
            {
                tunnelid = 0;
            }
        }
        var tunnelsw = new Stopwatch();
        tunnelsw.Start();
        var sw = new Stopwatch();
        sw.Start();
        var client = new TcpClient(config.server_address, config.server_port);
        client.ReceiveTimeout = 30000;
        client.SendTimeout = 30000;
        NetworkStream tcptunnel = client.GetStream();
        var encryptedStream = new SslStream(tcptunnel, true, userCertificateValidationCallback, userCertificateSelectionCallback);
        var disabled = false;
        encryptedStream.AuthenticateAsClient("", null, System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13, false);
        encryptedStream.Flush();
        while (true)
        {
            try
            {
                lock (tlock)
                {
                    while (senderqueue.Count > 0 && (DateTime.Now - senderqueue.FirstOrDefault().ts).TotalSeconds > 30)
                    {
                        TunnelSession tunnelSession = null;
                        senderqueue.TryDequeue(out tunnelSession);
                    }
                }
                //if ((tunnelsw.ElapsedMilliseconds > 30000 + tunnelid * 5000) && !disabled)
                //{
                //    tunnelsw.Restart();
                //    disabled = true;
                //    encryptedStream.WriteByte(0x11);
                //}
                //if (tunnelsw.ElapsedMilliseconds > 1000 && disabled)
                //{
                //    break;
                //}
                if (!client.Connected || sw.ElapsedMilliseconds > 30000)
                {
                    break;
                }
                while (client.Available > 0)
                {
                    sw.Restart();
                    var type = encryptedStream.ReadByte();
                    if (type == 0x02)
                    {
                        var sessionidbytes = new byte[8];
                        var orderbytes = new byte[8];
                        var lengthbytes = new byte[4];
                        var iplengthbytes = new byte[4];
                        encryptedStream.Read(iplengthbytes);
                        var iplength = BitConverter.ToInt32(iplengthbytes);
                        var ipbytes = new byte[iplength];
                        encryptedStream.Read(ipbytes);
                        var ip = Encoding.ASCII.GetString(ipbytes);
                        encryptedStream.Read(sessionidbytes);
                        var sessionid = BitConverter.ToUInt64(sessionidbytes);
                        encryptedStream.Read(orderbytes);
                        var order = BitConverter.ToUInt64(orderbytes);
                        encryptedStream.Read(lengthbytes);
                        var length = BitConverter.ToInt32(lengthbytes);
                        var buffer = new byte[65536];
                        var data = new List<byte>();
                        var totalread = 0;
                        //Console.WriteLine(sessionid + " " + order);
                        while (totalread < length)
                        {
                            var read = encryptedStream.Read(buffer, 0, length - totalread > buffer.Length ? buffer.Length : length - totalread);
                            data.AddRange(buffer.Take(read));
                            totalread += read;
                        }
                        if (responsesDict.ContainsKey(sessionid))
                        {
                            responsesDict[sessionid].Add(order, new Packet() { data = data.ToArray(), ts = DateTime.Now });
                        }
                        data.Clear();
                    }
                    else if (type == 0x04)
                    {
                        //Console.WriteLine("ack");
                        var sessionidbytes = new byte[8];
                        var iplengthbytes = new byte[4];
                        var orderbytes = new byte[8];
                        encryptedStream.Read(iplengthbytes);
                        var iplength = BitConverter.ToInt32(iplengthbytes);
                        var ipbytes = new byte[iplength];
                        encryptedStream.Read(ipbytes);
                        var ip = Encoding.ASCII.GetString(ipbytes);
                        encryptedStream.Read(sessionidbytes);
                        var sessionid = BitConverter.ToUInt64(sessionidbytes);
                        encryptedStream.Read(orderbytes);
                        var order = BitConverter.ToUInt64(orderbytes);
                        responsesDict[sessionid].Add(order, new Packet() { data = new byte[] { 0x04 }, ts = DateTime.Now });
                    }
                }
                lock (tlock)
                {
                    while (senderqueue.Count > 0 && !disabled)
                    {
                        sw.Restart();
                        TunnelSession tunnelSession = null;
                        senderqueue.TryDequeue(out tunnelSession);
                        var sessionidbytes = BitConverter.GetBytes(tunnelSession.ID);
                        if (tunnelSession.close)
                        {
                            //Console.WriteLine("drop: " + tunnelSession.ID);
                            var data = new List<byte>();
                            data.Add(0x03);
                            data.AddRange(BitConverter.GetBytes(0));
                            data.AddRange(sessionidbytes);
                            encryptedStream.Write(data.ToArray());
                            encryptedStream.Flush();
                            data.Clear();
                        }
                        else if (tunnelSession.ack)
                        {
                            var data = new List<byte>();
                            var orderbytes = BitConverter.GetBytes(tunnelSession.order);
                            data.Add(0x04);
                            data.AddRange(BitConverter.GetBytes(0));
                            data.AddRange(sessionidbytes);
                            data.AddRange(orderbytes);
                            encryptedStream.Write(data.ToArray());
                            encryptedStream.Flush();
                            data.Clear();
                        }
                        else
                        {
                            var orderbytes = BitConverter.GetBytes(tunnelSession.order);
                            var lengthbytes = BitConverter.GetBytes(tunnelSession.Data.Length);
                            var data = new List<byte>();
                            data.Add(0x02);
                            data.AddRange(BitConverter.GetBytes(0));
                            data.AddRange(sessionidbytes);
                            data.AddRange(orderbytes);
                            data.AddRange(lengthbytes);
                            data.AddRange(tunnelSession.Data);
                            encryptedStream.Write(data.ToArray());
                            encryptedStream.Flush();
                            data.Clear();
                        }
                    }
                }
                if (sw.ElapsedMilliseconds > 1000)
                {
                    sw.Restart();
                    encryptedStream.Write(new byte[] { 0x10 });
                    encryptedStream.Flush();
                }
                //if (!tcptunnel.DataAvailable)
                //{
                Thread.Sleep(1);
                //}
            }
            catch
            {

            }
        }
    }
    catch
    {

    }
    stableTunnelsCount--;
}
void ClientHandler(TcpClient client)
{
    string endpoint = "";
    var currentID = lastSessionID;
    lastSessionID++;
    if (lastSessionID >= ulong.MaxValue)
    {
        lastSessionID = 0;
    }
    try
    {
        int packetsforack = 0;
        ulong readorder = 1;
        ulong writeorder = 2;
        var buffer = new List<byte>();
        NetworkStream clientstream = client.GetStream();
        endpoint = clientstream.Socket.RemoteEndPoint.ToString();
        //Console.WriteLine(String.Format("Incoming Connection From: {0}", endpoint));
        bool sfirst = true, rfirst = true;
        var pinger = new Stopwatch();
        responsesDict.Add(currentID, new Dictionary<ulong, Packet>());
        var sw = new Stopwatch();
        sw.Start();
        ulong expectedack = 0;
        while (true)
        {
            try
            {
                if (!client.Connected || sw.ElapsedMilliseconds > 30000)
                    break;
                var read = 0;
                while (clientstream.DataAvailable && expectedack == 0)
                {
                    sw.Restart();
                    while (clientstream.DataAvailable && buffer.Count < 65536)
                    {
                        var bbbb = new byte[65536];
                        read = clientstream.Read(bbbb, 0, bbbb.Length);
                        buffer.AddRange(bbbb.Take(read));
                    }
                    //while (clientstream.DataAvailable && read != 0);
                    //encryptedStream.Write(buffer.ToArray());
                    senderqueue.Enqueue(new TunnelSession() { ID = currentID, Data = buffer.ToArray(), order = writeorder, ts = DateTime.Now });
                    //if ((buffer.Count >= 65536 && packetsforack >= 10) && (buffer.Count <= 0 && packetsforack >= 3))
                    {
                        expectedack = writeorder;
                    }
                    packetsforack++;
                    writeorder += 2;
                    if (writeorder >= ulong.MaxValue - 1)
                        writeorder = 2;
                    buffer.Clear();
                    if (sfirst)
                    {
                        pinger.Start();
                    }
                    sfirst = false;
                }
                lock (tlock)
                {
                    if (responsesDict[currentID].ContainsKey(expectedack))
                    {
                        responsesDict[currentID].Remove(expectedack);
                        expectedack = 0;
                        packetsforack = 0;
                    }
                    while (responsesDict[currentID].Count > 0 && responsesDict[currentID].ContainsKey(readorder))
                    {
                        sw.Restart();
                        byte[] bbbb = responsesDict[currentID][readorder].data;
                        responsesDict[currentID].Remove(readorder);
                        //do
                        //{
                        //    read = encryptedStream.Read(bbbb, 0, bbbb.Length);
                        //    buffer.AddRange(bbbb.Take(read));
                        //} while (tcptunnel.DataAvailable && read != 0);
                        clientstream.Write(bbbb);
                        clientstream.Flush();
                        senderqueue.Enqueue(new TunnelSession() { ID = currentID, ts = DateTime.Now, ack = true, order = readorder });
                        readorder += 2;
                        if (readorder >= ulong.MaxValue)
                            readorder = 1;
                        //Console.Write(Encoding.ASCII.GetString(buffer.ToArray()));
                        buffer.Clear();
                        if (rfirst)
                        {
                            Console.WriteLine("Request Ping {0}", pinger.ElapsedMilliseconds);
                            if (pinger.ElapsedMilliseconds > maxping)
                            {
                                maxping = pinger.ElapsedMilliseconds;
                            }
                            pinger.Stop();
                        }
                        rfirst = false;
                    }
                }
                Thread.Sleep(1);
            }
            catch
            {
                Thread.Sleep(1);
            }

        }


        senderqueue.Enqueue(new TunnelSession() { ID = currentID, ts = DateTime.Now, close = true });
        //encryptedStream.Close();
        //tcptunnel.Close();
        client.Close();
    }
    catch
    {

    }
    responsesDict.Remove(currentID);
    //Console.WriteLine(String.Format("Dropped Connection From: {0}", endpoint));
}

X509Certificate userCertificateSelectionCallback(object sender, string targetHost, X509CertificateCollection localCertificates, X509Certificate? remoteCertificate, string[] acceptableIssuers)
{
    return cert as X509Certificate;
}

var tcplistener = new TcpListener(System.Net.IPAddress.Any, config.proxy_listening_port);
tcplistener.Start();
while (true)
{
    if (tcplistener.Pending())
    {
        new Thread(() =>
        {
            ClientHandler(tcplistener.AcceptTcpClient());
        }).Start();
    }
    if (stableTunnelsCount == 0)
    {
        maxping = 0l;
        senderqueue.Clear();
        responsesDict.Clear();
    }
    lock (tlock)
    {
        if (stableTunnelsCount < maxStableTunnelsCount)
        {
            try
            {
                stableTunnelsCount++;
                new Thread(() =>
                {
                    StableTunnelHandler();
                }).Start();
                if (stableTunnelsCount >= maxStableTunnelsCount)
                {
                    firsttunnelsinit = false;
                }
            }
            catch
            {

            }
        }
    }
    Thread.Sleep(100);
}