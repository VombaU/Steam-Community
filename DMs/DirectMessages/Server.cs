﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Google.Protobuf;

namespace DirectMessages
{
    internal class Server
    {
        private Socket serverSocket;
        private IPEndPoint ipEndPoint;
        private System.Threading.Timer? serverTimeout;
        private readonly object lockTimer;

        private ConcurrentDictionary<String, String> addressesAndUserNames;
        private ConcurrentDictionary<Socket, String> connectedClients;
        private ConcurrentBag<String> adminUsers;
        private ConcurrentBag<String> mutedUsers;

        private Regex muteRegex;
        private Regex adminRegex;
        private Regex kickRegex;

        private String hostName;
        private String muteCommandPattern;
        private String adminCommandPattern;
        private String kickCommandPattern;

        private bool isRunning;

        const int PORT_NUMBER = 6000;
        const int MESSAGE_MAXIMUM_SIZE = 4112;
        const int NUMBER_OF_QUEUED_CONNECTIONS = 10;
        const int STARTING_INDEX = 0;
        const int DISCONNECT_CODE = 0;
        const int SERVER_TIMEOUT_COUNTDOWN = 180000;
        const int MINIMUM_CONNECTIONS = 2;
        const char ADDRESS_SEPARATOR = ':';

        public Server(String hostAddress, String hostName)
        {
            this.muteCommandPattern = @"^<.*>\|Mute\|<.*>$";
            this.adminCommandPattern = @"^<.*>\|Admin\|<.*>$";
            this.kickCommandPattern = @"^<.*>\|Kick\|<.*>$";

            this.muteRegex = new Regex(this.muteCommandPattern);
            this.adminRegex = new Regex(this.adminCommandPattern);
            this.kickRegex = new Regex(this.kickCommandPattern);

            this.addressesAndUserNames = new ConcurrentDictionary<string, string>();
            this.connectedClients = new ConcurrentDictionary<Socket, string>();

            this.lockTimer = new object();

            this.hostName = hostName;

            try
            {
                this.ipEndPoint = new IPEndPoint(IPAddress.Parse(hostAddress), PORT_NUMBER);
                this.serverSocket = new(this.ipEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                this.serverSocket.Bind(this.ipEndPoint);
                this.serverSocket.Listen(NUMBER_OF_QUEUED_CONNECTIONS);
            }
            catch (Exception exception)
            {
                throw new ServerException($"Server create error: {exception.Message}");
            }

            this.isRunning = true;
        }

        public async void Start()
        {
            while (this.isRunning)
            {
                try
                {
                    Socket clientSocket = await this.serverSocket.AcceptAsync();

                    String socketNullResult = "Disconnected";
                    String ipAddressAndPort = clientSocket.RemoteEndPoint?.ToString() ?? socketNullResult;

                    String ipAddress = ipAddressAndPort.Substring(STARTING_INDEX, ipAddressAndPort.IndexOf(ADDRESS_SEPARATOR));
                    Debug.Write(ipAddress);
                    this.connectedClients.TryAdd(clientSocket, ipAddress);

                    this.CheckForMinimumConnections();

                    _ = Task.Run(() => HandleClient(clientSocket));
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Client couldn't connect: {exception.Message}");
                }
            }
        }

        private async Task HandleClient(Socket clientSocket)
        {
            try
            {
                byte[] userNameBuffer = new byte[MESSAGE_MAXIMUM_SIZE];
                int userNameLength = await clientSocket.ReceiveAsync(userNameBuffer, SocketFlags.None);

                String userName = Encoding.UTF8.GetString(userNameBuffer, STARTING_INDEX, userNameLength);
                String ipAddress = this.connectedClients.GetValueOrDefault(clientSocket) ?? "";
                this.addressesAndUserNames.TryAdd(ipAddress, userName);

                while (this.isRunning)
                {
                    byte[] messageBuffer = new byte[MESSAGE_MAXIMUM_SIZE];
                    int charactersReceivedCount = await clientSocket.ReceiveAsync(messageBuffer, SocketFlags.None);

                    if(!this.isRunning)
                    {
                        break;
                    }

                    String messageContentReceived = Encoding.UTF8.GetString(messageBuffer, STARTING_INDEX, charactersReceivedCount);

                    if (charactersReceivedCount == DISCONNECT_CODE)
                    {
                        switch (this.IsHost(ipAddress))
                        {
                            case true:
                                messageContentReceived = "Host disconnected";
                                this.SendMessageToClients(CreateMessage(messageContentReceived, userName));
                                this.ShutDownServer();
                                break;
                            case false:
                                messageContentReceived = "Disconnected";
                                this.SendMessageToClients(CreateMessage(messageContentReceived, userName));
                                this.CheckForMinimumConnections();
                                break;
                        }

                        this.addressesAndUserNames.TryRemove(ipAddress, out _);
                        this.connectedClients.TryRemove(clientSocket, out _);
                        break;
                    }

                    if(this.CheckRegexMatch(this.muteRegex, this.mutedUsers, messageContentReceived, userName, "has been muted"))
                    {
                        continue;
                    }

                    if(this.CheckRegexMatch(this.adminRegex, this.adminUsers, messageContentReceived, userName, "has been promoted to admin"))
                    {
                        continue;
                    }

                    this.SendMessageToClients(CreateMessage(messageContentReceived, userName));
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Client had an error: {exception.Message}");
            }
            finally
            {
                clientSocket.Shutdown(SocketShutdown.Both);
                clientSocket.Close();
            }
        }

        private Message CreateMessage(String contentMessage, String userName)
        {
            Message message = new Message
            {
                MessageContent = contentMessage,
                MessageDateTime = DateTime.Now.ToString(),
                MessageSenderName = userName,
                MessageAligment = "Left",
                MessageSenderStatus = this.GetHighestStatus(userName),
            };

            return message;
        }

        private void SendMessageToClients(Message message)
        {
            foreach (KeyValuePair<Socket, String> clientAddress in this.connectedClients)
            {
                byte[] messageBytes = message.ToByteArray();
                clientAddress.Key.SendAsync(messageBytes, SocketFlags.None);
            }
        }

        private bool IsHost(String ipAddress)
        {
            return ipAddress == ipEndPoint.Address.ToString();
        }

        private void InitializeServerTimeout()
        {
            lock (this.lockTimer)
            {
                this.serverTimeout?.Dispose();
                this.serverTimeout = new System.Threading.Timer((_) =>
                {
                    if (connectedClients.Count < MINIMUM_CONNECTIONS)
                    {
                        this.ShutDownServer();
                    }
                }, null, SERVER_TIMEOUT_COUNTDOWN, System.Threading.Timeout.Infinite);
            }
        }

        private void ShutDownServer()
        {
            if(this.serverSocket.Connected == true)
            {
                this.serverSocket.Shutdown(SocketShutdown.Both);
            }
            this.serverSocket.Close();
            this.isRunning = false;
        }

        private void CheckForMinimumConnections()
        {
            if (this.connectedClients.Count < MINIMUM_CONNECTIONS)
            {
                this.InitializeServerTimeout();
            }
        }

        public bool IsServerRunning()
        {
            return this.isRunning;
        }

        private String GetHighestStatus(String userName)
        {
            switch (true)
            {
                case true when this.hostName == userName:
                    return "Host";
                case true when this.adminUsers.Contains(userName):
                    return "Admin";
                default:
                    return "User";
            }
        }

        private bool IsHighetStatus(String firstUserStatus, String secondUserStatus)
        {
            return (firstUserStatus == "Host" && secondUserStatus != "Host") || (firstUserStatus == "Admin" && secondUserStatus == "User");
        }
        
        private String FindTargetedUserNameFromCommand(String Command)
        {
            int CommandTargetIndex = 2;
            String commandTarget = Command.Split('|')[CommandTargetIndex];

            int NameStartIndex = 1, NameEndIndex = commandTarget.Length - 2;
            String targetedUserName = commandTarget.Substring(NameStartIndex, NameEndIndex);

            return targetedUserName;
        }

        private bool CheckRegexMatch(Regex regex, ConcurrentBag<String> statusHolder, String message, String userName, String serverMessage)
        {
            if (regex.IsMatch(message))
            {
                String targetedUserName = this.FindTargetedUserNameFromCommand(message);

                if (this.IsHighetStatus(this.GetHighestStatus(userName), this.GetHighestStatus(targetedUserName)))
                {
                    statusHolder.Add(targetedUserName);
                    this.SendMessageToClients(CreateMessage($"{targetedUserName} " + serverMessage, hostName));
                }

                return true;
            }
            return false;
        }
    }
}
