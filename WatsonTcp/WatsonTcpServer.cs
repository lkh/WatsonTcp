﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using WatsonTcp.Message;

namespace WatsonTcp
{
    /// <summary>
    /// Watson TCP server, with or without SSL.
    /// </summary>
    public class WatsonTcpServer : IDisposable
    {
        #region Public-Members

        /// <summary>
        /// Enable or disable console debugging.
        /// </summary>
        public bool Debug = false;

        /// <summary>
        /// Permitted IP addresses.
        /// </summary>
        public List<string> PermittedIPs = null;

        /// <summary>
        /// Method to call when a client connects to the server.  
        /// The IP:port is passed to this method as a string, and it is expected that the method will return true.
        /// </summary>
        public Func<string, bool> ClientConnected = null;

        /// <summary>
        /// Method to call when a client disconnects from the server.  
        /// The IP:port is passed to this method as a string, and it is expected that the method will return true.
        /// </summary>
        public Func<string, bool> ClientDisconnected = null;

        /// <summary>
        /// Method to call when a message is received from a client.  
        /// The IP:port is passed to this method as a string, along with a byte array containing the message data.  
        /// It is expected that the method will return true.
        /// </summary>
        public Func<string, byte[], bool> MessageReceived = null;

        /// <summary>
        /// Enable acceptance of SSL certificates from clients that cannot be validated.
        /// </summary>
        public bool AcceptInvalidCertificates = true;

        /// <summary>
        /// Require mutual authentication between SSL clients and this server.
        /// </summary>
        public bool MutuallyAuthenticate = false;

        /// <summary>
        /// Preshared key that must be consistent between clients and this server.
        /// </summary>
        public string PresharedKey = null;

        #endregion

        #region Private-Members

        private bool _Disposed = false; 
        private Mode _Mode;  
        private string _ListenerIp;
        private int _ListenerPort; 
        private IPAddress _ListenerIpAddress;
        private TcpListener _Listener;

        private X509Certificate2 _SslCertificate;

        private int _ActiveClients;
        private ConcurrentDictionary<string, ClientMetadata> _Clients;
        private ConcurrentDictionary<string, DateTime> _UnauthenticatedClients;

        private readonly SemaphoreSlim _SendLock;
        private CancellationTokenSource _TokenSource;
        private CancellationToken _Token;

        #endregion

        #region Constructors-and-Factories
         
        /// <summary>
        /// Initialize the Watson TCP server without SSL.  Call Start() afterward to start Watson.
        /// </summary>
        /// <param name="listenerIp">The IP address on which the server should listen, nullable.</param>
        /// <param name="listenerPort">The TCP port on which the server should listen.</param>
        public WatsonTcpServer(
            string listenerIp,
            int listenerPort)
        {
            if (listenerPort < 1) throw new ArgumentOutOfRangeException(nameof(listenerPort));

            _Mode = Mode.Tcp;

            if (String.IsNullOrEmpty(listenerIp))
            {
                _ListenerIpAddress = IPAddress.Any;
                _ListenerIp = _ListenerIpAddress.ToString();
            }
            else
            {
                _ListenerIpAddress = IPAddress.Parse(listenerIp);
                _ListenerIp = listenerIp;
            }

            _ListenerPort = listenerPort;
            
            _Listener = new TcpListener(_ListenerIpAddress, _ListenerPort);

            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;

            _ActiveClients = 0;
            _Clients = new ConcurrentDictionary<string, ClientMetadata>();
            _UnauthenticatedClients = new ConcurrentDictionary<string, DateTime>();
            _SendLock = new SemaphoreSlim(1);
        }
        
        /// <summary>
        /// Initialize the Watson TCP server with SSL.  Call Start() afterward to start Watson.
        /// </summary>
        /// <param name="listenerIp">The IP address on which the server should listen, nullable.</param>
        /// <param name="listenerPort">The TCP port on which the server should listen.</param>
        /// <param name="pfxCertFile">The file containing the SSL certificate.</param>
        /// <param name="pfxCertPass">The password for the SSL certificate.</param>
        public WatsonTcpServer(
            string listenerIp,
            int listenerPort,
            string pfxCertFile,
            string pfxCertPass)
        {
            if (listenerPort < 1) throw new ArgumentOutOfRangeException(nameof(listenerPort));

            _Mode = Mode.Ssl;

            if (String.IsNullOrEmpty(listenerIp))
            {
                _ListenerIpAddress = IPAddress.Any;
                _ListenerIp = _ListenerIpAddress.ToString();
            }
            else
            {
                _ListenerIpAddress = IPAddress.Parse(listenerIp);
                _ListenerIp = listenerIp;
            }

            _ListenerPort = listenerPort;

            _SslCertificate = null;
            if (String.IsNullOrEmpty(pfxCertPass))
            {
                _SslCertificate = new X509Certificate2(pfxCertFile);
            }
            else
            {
                _SslCertificate = new X509Certificate2(pfxCertFile, pfxCertPass);
            }

            _Listener = new TcpListener(_ListenerIpAddress, _ListenerPort);
            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;
            _ActiveClients = 0;
            _Clients = new ConcurrentDictionary<string, ClientMetadata>();
            _UnauthenticatedClients = new ConcurrentDictionary<string, DateTime>();
            _SendLock = new SemaphoreSlim(1); 
        }
        
        #endregion

        #region Public-Methods

        /// <summary>
        /// Tear down the server and dispose of background workers.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Start the server.  
        /// </summary>
        public void Start()
        {
            if (_Mode == Mode.Tcp)
            {
                Log("Watson TCP server starting on " + _ListenerIp + ":" + _ListenerPort);
            }
            else if (_Mode == Mode.Ssl)
            {
                Log("Watson TCP SSL server starting on " + _ListenerIp + ":" + _ListenerPort);
            }
            else
            {
                throw new ArgumentException("Unknown mode: " + _Mode.ToString());
            }

            Task.Run(() => AcceptConnections(), _Token);
        }

        /// <summary>
        /// Send data to the specified client.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="data">Byte array containing data.</param>
        /// <returns>Boolean indicating if the message was sent successfully.</returns>
        public bool Send(string ipPort, byte[] data)
        {
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Log("*** Send unable to find client " + ipPort);
                return false;
            }

            WatsonMessage msg = new WatsonMessage(data, Debug);
            return MessageWrite(client, msg);
        }

        /// <summary>
        /// Send data to the specified client.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="msg">Populated message object.</param>
        /// <returns>Boolean indicating if the message was sent successfully.</returns>
        public bool Send(string ipPort, WatsonMessage msg)
        {
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Log("*** Send unable to find client " + ipPort);
                return false;
            }

            return MessageWrite(client, msg);
        }

        /// <summary>
        /// Send data to the specified client, asynchronously.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="data">Byte array containing data.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public async Task<bool> SendAsync(string ipPort, byte[] data)
        {
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Log("*** SendAsync unable to find client " + ipPort);
                return false;
            }

            WatsonMessage msg = new WatsonMessage(data, Debug);
            return await MessageWriteAsync(client, msg);
        }

        /// <summary>
        /// Send data to the specified client, asynchronously.
        /// </summary>
        /// <param name="ipPort">IP:port of the recipient client.</param>
        /// <param name="msg">Populated message object.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public async Task<bool> SendAsync(string ipPort, WatsonMessage msg)
        {
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Log("*** SendAsync unable to find client " + ipPort);
                return false;
            }

            return await MessageWriteAsync(client, msg);
        }

        /// <summary>
        /// Determine whether or not the specified client is connected to the server.
        /// </summary>
        /// <returns>Boolean indicating if the client is connected to the server.</returns>
        public bool IsClientConnected(string ipPort)
        {
            return (_Clients.TryGetValue(ipPort, out ClientMetadata client));
        }

        /// <summary>
        /// List the IP:port of each connected client.
        /// </summary>
        /// <returns>A string list containing each client IP:port.</returns>
        public List<string> ListClients()
        {
            Dictionary<string, ClientMetadata> clients = _Clients.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            List<string> ret = new List<string>();
            foreach (KeyValuePair<string, ClientMetadata> curr in clients)
            {
                ret.Add(curr.Key);
            }
            return ret;
        }

        /// <summary>
        /// Disconnects the specified client.
        /// </summary>
        public void DisconnectClient(string ipPort)
        {
            if (!_Clients.TryGetValue(ipPort, out ClientMetadata client))
            {
                Log("*** DisconnectClient unable to find client " + ipPort);
            }
            else
            {
                client.Dispose();
            }
        }

        #endregion

        #region Private-Methods

        protected virtual void Dispose(bool disposing)
        {
            if (_Disposed)
            {
                return;
            }

            if (disposing)
            {
                _TokenSource.Cancel();
                _TokenSource.Dispose();

                if (_Listener != null && _Listener.Server != null)
                {
                    _Listener.Server.Close();
                    _Listener.Server.Dispose();
                }

                if (_Clients != null && _Clients.Count > 0)
                {
                    foreach (KeyValuePair<string, ClientMetadata> currMetadata in _Clients)
                    {
                        currMetadata.Value.Dispose();
                    }
                }
            }

            _SendLock.Dispose(); 
            _Disposed = true;
        }

        private void Log(string msg)
        {
            if (Debug) Console.WriteLine(msg);
        }

        private void LogException(string method, Exception e)
        {
            Log("================================================================================");
            Log(" = Method: " + method);
            Log(" = Exception Type: " + e.GetType().ToString());
            Log(" = Exception Data: " + e.Data);
            Log(" = Inner Exception: " + e.InnerException);
            Log(" = Exception Message: " + e.Message);
            Log(" = Exception Source: " + e.Source);
            Log(" = Exception StackTrace: " + e.StackTrace);
            Log("================================================================================");
        }

        private bool AcceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // return true; // Allow untrusted certificates.
            return AcceptInvalidCertificates;
        }

        private async Task AcceptConnections()
        {
            _Listener.Start();
            while (!_Token.IsCancellationRequested)
            {
                string clientIpPort = String.Empty;

                try
                {
                    #region Accept-Connection-and-Validate-IP

                    TcpClient tcpClient = await _Listener.AcceptTcpClientAsync();
                    tcpClient.LingerState.Enabled = false;
                    
                    string clientIp = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                    if (PermittedIPs != null && PermittedIPs.Count > 0)
                    {
                        if (!PermittedIPs.Contains(clientIp))
                        {
                            Log("*** AcceptConnections rejecting connection from " + clientIp + " (not permitted)");
                            tcpClient.Close();
                            continue;
                        }
                    }

                    ClientMetadata client = new ClientMetadata(tcpClient);
                    clientIpPort = client.IpPort;

                    #endregion

                    if (_Mode == Mode.Tcp)
                    {
                        #region Tcp

                        Task unawaited = Task.Run(() =>
                        {
                            FinalizeConnection(client);
                        }, _Token);

                        #endregion
                    }
                    else if (_Mode == Mode.Ssl)
                    {
                        #region SSL

                        if (AcceptInvalidCertificates)
                        {
                            client.SslStream = new SslStream(client.NetworkStream, false, new RemoteCertificateValidationCallback(AcceptCertificate));
                        }
                        else
                        {
                            client.SslStream = new SslStream(client.NetworkStream, false);
                        }

                        Task unawaited = Task.Run(() => {
                            Task<bool> success = StartTls(client);
                            if (success.Result)
                            {
                                FinalizeConnection(client);
                            }
                        }, _Token);

                        #endregion
                    }
                    else
                    {
                        throw new ArgumentException("Unknown mode: " + _Mode.ToString());
                    }
                     
                    Log("*** AcceptConnections accepted connection from " + client.IpPort);
                }
                catch (Exception e)
                {
                    Log("*** AcceptConnections exception " + clientIpPort + " " + e.Message);
                }
            }
        }

        private async Task<bool> StartTls(ClientMetadata client)
        {
            try
            {
                // the two bools in this should really be contruction paramaters
                // maybe re-use mutualAuthentication and acceptInvalidCerts ?
                await client.SslStream.AuthenticateAsServerAsync(_SslCertificate, true, SslProtocols.Tls12, false);

                if (!client.SslStream.IsEncrypted)
                {
                    Log("*** StartTls stream from " + client.IpPort + " not encrypted");
                    client.Dispose();
                    return false;
                }

                if (!client.SslStream.IsAuthenticated)
                {
                    Log("*** StartTls stream from " + client.IpPort + " not authenticated");
                    client.Dispose();
                    return false;
                }

                if (MutuallyAuthenticate && !client.SslStream.IsMutuallyAuthenticated)
                {
                    Log("*** StartTls stream from " + client.IpPort + " failed mutual authentication");
                    client.Dispose();
                    return false;
                }
            }
            catch (IOException ex)
            {
                // Some type of problem initiating the SSL connection
                switch (ex.Message)
                {
                    case "Authentication failed because the remote party has closed the transport stream.":
                    case "Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.":
                        Log("*** StartTls IOException " + client.IpPort + " closed the connection.");
                        break;
                    case "The handshake failed due to an unexpected packet format.":
                        Log("*** StartTls IOException " + client.IpPort + " disconnected, invalid handshake.");
                        break;
                    default:
                        Log("*** StartTls IOException from " + client.IpPort + Environment.NewLine + ex.ToString());
                        break;
                }

                client.Dispose();
                return false;
            }
            catch (Exception ex)
            {
                Log("*** StartTls Exception from " + client.IpPort + Environment.NewLine + ex.ToString());
                client.Dispose();
                return false;
            }

            return true;
        }

        private void FinalizeConnection(ClientMetadata client)
        {
            #region Add-to-Client-List

            if (!AddClient(client))
            {
                Log("*** FinalizeConnection unable to add client " + client.IpPort);
                client.Dispose();
                return;
            }

            // Do not decrement in this block, decrement is done by the connection reader
            int activeCount = Interlocked.Increment(ref _ActiveClients);

            #endregion

            #region Request-Authentication

            if (!String.IsNullOrEmpty(PresharedKey))
            {
                Log("*** FinalizeConnection soliciting authentication material from " + client.IpPort);
                _UnauthenticatedClients.TryAdd(client.IpPort, DateTime.Now);

                byte[] data = Encoding.UTF8.GetBytes("Authentication required");
                WatsonMessage authMsg = new WatsonMessage(data, Debug);
                authMsg.Status = MessageStatus.AuthRequired;
                Send(client.IpPort, authMsg);
            }

            #endregion

            #region Start-Data-Receiver

            Log("*** FinalizeConnection starting data receiver for " + client.IpPort + " (now " + activeCount + " clients)");
            if (ClientConnected != null)
            {
                Task.Run(() => ClientConnected(client.IpPort));
            }

            Task.Run(async () => await DataReceiver(client));

            #endregion 
        }

        private bool IsConnected(ClientMetadata client)
        {
            if (client.TcpClient.Connected)
            {
                if ((client.TcpClient.Client.Poll(0, SelectMode.SelectWrite)) 
                    && (!client.TcpClient.Client.Poll(0, SelectMode.SelectError)))
                {
                    byte[] buffer = new byte[1];
                    if (client.TcpClient.Client.Receive(buffer, SocketFlags.Peek) == 0)
                    {
                        return false;
                    }
                    else
                    {
                        return true;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }
        }

        private async Task DataReceiver(ClientMetadata client)
        {
            try
            {
                #region Wait-for-Data

                while (true)
                {
                    try
                    {
                        if (!IsConnected(client))
                        {
                            break;
                        }

                        WatsonMessage msg = await MessageReadAsync(client);
                        if (msg == null)
                        {
                            // no message available
                            await Task.Delay(30);
                            continue;
                        }

                        if (!String.IsNullOrEmpty(PresharedKey))
                        {
                            if (_UnauthenticatedClients.ContainsKey(client.IpPort))
                            { 
                                Log("*** DataReceiver message received from unauthenticated endpoint: " + client.IpPort);

                                if (msg.Status == MessageStatus.AuthRequired)
                                {
                                    // check preshared key 
                                    if (msg.PresharedKey != null && msg.PresharedKey.Length > 0)
                                    {
                                        string clientPsk = Encoding.UTF8.GetString(msg.PresharedKey).Trim();
                                        if (PresharedKey.Trim().Equals(clientPsk))
                                        {
                                            if (Debug) Log("DataReceiver accepted authentication from " + client.IpPort);
                                            _UnauthenticatedClients.TryRemove(client.IpPort, out DateTime dt);
                                            byte[] data = Encoding.UTF8.GetBytes("Authentication successful");
                                            WatsonMessage authMsg = new WatsonMessage(data, Debug);
                                            authMsg.Status = MessageStatus.AuthSuccess;
                                            Send(client.IpPort, authMsg);
                                            continue;
                                        }
                                        else
                                        {
                                            if (Debug) Log("DataReceiver declined authentication from " + client.IpPort);
                                            byte[] data = Encoding.UTF8.GetBytes("Authentication declined");
                                            WatsonMessage authMsg = new WatsonMessage(data, Debug);
                                            authMsg.Status = MessageStatus.AuthFailure;
                                            Send(client.IpPort, authMsg);
                                            continue;
                                        }
                                    }
                                    else
                                    {
                                        if (Debug) Log("DataReceiver no authentication material from " + client.IpPort);
                                        byte[] data = Encoding.UTF8.GetBytes("No authentication material");
                                        WatsonMessage authMsg = new WatsonMessage(data, Debug);
                                        authMsg.Status = MessageStatus.AuthFailure;
                                        Send(client.IpPort, authMsg);
                                        continue;
                                    }
                                }
                                else
                                {
                                    // decline the message
                                    if (Debug) Log("DataReceiver no authentication material from " + client.IpPort);
                                    byte[] data = Encoding.UTF8.GetBytes("Authentication required");
                                    WatsonMessage authMsg = new WatsonMessage(data, Debug);
                                    authMsg.Status = MessageStatus.AuthRequired;
                                    Send(client.IpPort, authMsg);
                                    continue;
                                }
                            }
                        }

                        if (MessageReceived != null)
                        {
                            Task<bool> unawaited = Task.Run(() => MessageReceived(client.IpPort, msg.Data));
                        }
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }

                #endregion
            }
            finally
            {
                int activeCount = Interlocked.Decrement(ref _ActiveClients);
                RemoveClient(client);
                if (ClientDisconnected != null)
                {
                    Task<bool> unawaited = Task.Run(() => ClientDisconnected(client.IpPort));
                }
                Log("*** DataReceiver client " + client.IpPort + " disconnected (now " + activeCount + " clients active)");

                client.Dispose();
            }
        }

        private bool AddClient(ClientMetadata client)
        {
            _Clients.TryRemove(client.IpPort, out ClientMetadata removedClient);
            _Clients.TryAdd(client.IpPort, client);

            Log("*** AddClient added client " + client.IpPort);
            return true;
        }

        private bool RemoveClient(ClientMetadata client)
        {
            _Clients.TryRemove(client.IpPort, out ClientMetadata removedClient);
            _UnauthenticatedClients.TryRemove(client.IpPort, out DateTime dt);

            Log("*** RemoveClient removed client " + client.IpPort);
            return true;
        }
         
        private async Task<WatsonMessage> MessageReadAsync(ClientMetadata client)
        {
            /*
             *
             * Do not catch exceptions, let them get caught by the data reader
             * to destroy the connection
             *
             */

            WatsonMessage msg = null;

            if (_Mode == Mode.Ssl)
            {
                msg = new WatsonMessage(client.SslStream, Debug);
                await msg.Build();
            }
            else if (_Mode == Mode.Tcp)
            {
                msg = new WatsonMessage(client.NetworkStream, Debug);
                await msg.Build();
            }
            else
            {
                throw new ArgumentException("Unknown mode: " + _Mode.ToString());
            }

            return msg; 
        }

        private bool MessageWrite(ClientMetadata client, WatsonMessage msg)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (msg == null) throw new ArgumentNullException(nameof(msg));

            byte[] msgBytes = msg.ToBytes();

            _SendLock.Wait();

            try
            {
                if (_Mode == Mode.Tcp)
                {
                    client.NetworkStream.Write(msgBytes, 0, msgBytes.Length);
                    client.NetworkStream.Flush();
                }
                else if (_Mode == Mode.Ssl)
                {
                    client.SslStream.Write(msgBytes, 0, msgBytes.Length);
                    client.SslStream.Flush();
                }
                else
                {
                    throw new ArgumentException("Unknown mode: " + _Mode.ToString());
                } 

                return true;
            }
            catch (Exception)
            {
                Log("*** MessageWrite " + client.IpPort + " disconnected due to exception");
                return false;
            }
            finally
            {
                _SendLock.Release();
            }
        }

        private async Task<bool> MessageWriteAsync(ClientMetadata client, WatsonMessage msg)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (msg == null) throw new ArgumentNullException(nameof(msg));

            byte[] msgBytes = msg.ToBytes();

            await _SendLock.WaitAsync();

            try
            {
                if (_Mode == Mode.Tcp)
                {
                    await client.NetworkStream.WriteAsync(msgBytes, 0, msgBytes.Length);
                    await client.NetworkStream.FlushAsync();
                }
                else if (_Mode == Mode.Ssl)
                {
                    await client.SslStream.WriteAsync(msgBytes, 0, msgBytes.Length);
                    await client.SslStream.FlushAsync();
                }
                else
                {
                    throw new ArgumentException("Unknown mode: " + _Mode.ToString());
                } 

                return true;
            }
            catch (Exception)
            {
                Log("*** MessageWriteAsync " + client.IpPort + " disconnected due to exception");
                return false;
            }
            finally
            {
                _SendLock.Release();
            }
        }

        #endregion
    }
}
