﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        private ConcurrentDictionary<String, bool> mutedUsers;
        private ConcurrentDictionary<String, bool> adminUsers;

        private Regex muteCommandRegex;
        private Regex adminCommandRegex;
        private Regex kickCommandRegex;
        private Regex infoChangeCommandRegex;

        private String hostName;
        private String muteCommandPattern;
        private String adminCommandPattern;
        private String kickCommandPattern;
        private String infoCommandPattern;

        private bool isRunning;

        // Port number is always the same
        public const int PORT_NUMBER = 6000;

        public const int MESSAGE_MAXIMUM_SIZE = 4112;
        public const int USER_NAME_MAXIMUM_SIZE = 512;
        public const int NUMBER_OF_QUEUED_CONNECTIONS = 10;
        public const int STARTING_INDEX = 0;
        public const int DISCONNECT_CODE = 0;
        public const int SERVER_TIMEOUT_COUNTDOWN = 180000;
        public const int MINIMUM_CONNECTIONS = 2;
        public const char ADDRESS_SEPARATOR = ':';
        public const String ADMIN_STATUS = "ADMIN";
        public const String MUTE_STATUS = "MUTE";
        public const String KICK_STATUS = "KICK";
        public const String HOST_STATUS = "HOST";
        public const String REGULAR_USER_STATUS = "USER";
        public const String INFO_CHANGE_MUTE_STATUS_COMMAND = "<INFO>|" + MUTE_STATUS + "|<INFO>";
        public const String INFO_CHANGE_ADMIN_STATUS_COMMAND = "<INFO>|" + ADMIN_STATUS + "|<INFO>";
        public const String INFO_CHANGE_KICK_STATUS_COMMAND = "<INFO>|" + KICK_STATUS + "|<INFO>";

        /// <summary>
        /// Constructor for the server class
        /// </summary>
        /// <param name="hostAddress">Address of the host</param>
        /// <param name="hostName">Name of the host</param>
        /// <exception cref="Exception">Server Creation Error</exception>
        public Server(String hostAddress, String hostName)
        {
            this.muteCommandPattern = @"^<.*>\|Mute\|<.*>$";
            this.adminCommandPattern = @"^<.*>\|Admin\|<.*>$";
            this.kickCommandPattern = @"^<.*>\|Kick\|<.*>$";
            this.infoCommandPattern = @"^<INFO>\|.*\|<INFO>$";

            this.muteCommandRegex = new Regex(this.muteCommandPattern);
            this.adminCommandRegex = new Regex(this.adminCommandPattern);
            this.kickCommandRegex = new Regex(this.kickCommandPattern);
            this.infoChangeCommandRegex = new Regex(this.infoCommandPattern);

            this.addressesAndUserNames = new ConcurrentDictionary<string, string>();
            this.connectedClients = new ConcurrentDictionary<Socket, string>();
            this.mutedUsers = new ConcurrentDictionary<string, bool>();
            this.adminUsers = new ConcurrentDictionary<string, bool>();

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
                throw new Exception($"Server create error: {exception.Message}");
            }

            this.isRunning = true;
        }

        /// <summary>
        /// Listen to connections to the server
        /// </summary>
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

                    this.connectedClients.TryAdd(clientSocket, ipAddress);

                    this.CheckForMinimumConnections();

                    // A new thread for every client
                    _ = Task.Run(() => HandleClient(clientSocket));
                }
                catch (Exception exception)
                {
                    // At most show the developer the error, don't crash the server
                    Debug.WriteLine($"Client couldn't connect: {exception.Message}");
                }
            }
        }

        /// <summary>
        /// Receives messages / commands from a client
        /// </summary>
        /// <param name="clientSocket">Current client socket</param>
        /// <returns>A promise</returns>
        private async Task HandleClient(Socket clientSocket)
        {
            try
            {
                byte[] userNameBuffer = new byte[USER_NAME_MAXIMUM_SIZE];
                int userNameLength = await clientSocket.ReceiveAsync(userNameBuffer, SocketFlags.None);

                String userName = Encoding.UTF8.GetString(userNameBuffer, STARTING_INDEX, userNameLength);
                String ipAddress = this.connectedClients.GetValueOrDefault(clientSocket) ?? "";

                this.addressesAndUserNames.TryAdd(ipAddress, userName);
                this.adminUsers.TryAdd(userName, false);
                this.mutedUsers.TryAdd(userName, false);

                while (this.isRunning)
                {
                    byte[] messageBuffer = new byte[MESSAGE_MAXIMUM_SIZE];
                    int charactersReceivedCount = await clientSocket.ReceiveAsync(messageBuffer, SocketFlags.None);

                    // In case of a timeout between waiting for messages
                    if(!this.isRunning)
                    {
                        break;
                    }

                    String messageContentReceived = Encoding.UTF8.GetString(messageBuffer, STARTING_INDEX, charactersReceivedCount);

                    // Don't allow users to change info, the server does that
                    if (this.infoChangeCommandRegex.IsMatch(messageContentReceived))
                    {
                        continue;
                    }

                    if (charactersReceivedCount == DISCONNECT_CODE)
                    {
                        switch (this.IsHost(ipAddress))
                        {
                            case true:
                                messageContentReceived = "Host disconnected";
                                this.SendMessageToAllClients(CreateMessage(messageContentReceived, userName));
                                this.ShutDownServer();
                                break;
                            case false:
                                messageContentReceived = "Disconnected";
                                this.SendMessageToAllClients(CreateMessage(messageContentReceived, userName));
                                this.CheckForMinimumConnections();
                                break;
                        }

                        this.RemoveClientInformation(clientSocket, userName, ipAddress);
                        break;
                    }

                    bool commandFound = true, hasBeenKicked = false;

                    switch (true)
                    {
                        case true when this.muteCommandRegex.IsMatch(messageContentReceived):
                            this.TryChangeStatus(messageContentReceived, MUTE_STATUS, userName, this.mutedUsers);
                            this.SendMessageToOneClient(CreateMessage(INFO_CHANGE_MUTE_STATUS_COMMAND, userName), clientSocket);
                            break;
                        case true when this.adminCommandRegex.IsMatch(messageContentReceived):
                            this.TryChangeStatus(messageContentReceived, ADMIN_STATUS, userName, this.adminUsers);
                            this.SendMessageToOneClient(CreateMessage(INFO_CHANGE_ADMIN_STATUS_COMMAND, userName), clientSocket);
                            break;
                        case true when this.kickCommandRegex.IsMatch(messageContentReceived):
                            this.TryChangeStatus(messageContentReceived, KICK_STATUS, userName);
                            this.SendMessageToOneClient(CreateMessage(INFO_CHANGE_KICK_STATUS_COMMAND, userName), clientSocket);
                            this.RemoveClientInformation(clientSocket, userName, ipAddress);
                            hasBeenKicked = true;
                            break;
                        default:
                            commandFound = false;
                            break;
                    }

                    if(hasBeenKicked)
                    {
                        break;
                    }

                    // Don't send the command to users
                    if (commandFound)
                    { 
                        continue;
                    }

                    this.SendMessageToAllClients(CreateMessage(messageContentReceived, userName));
                }
            }
            catch (Exception exception)
            {
                // Exception is not catched (doesn't crash the main thread),
                // so at least show the developer the error
                Debug.WriteLine($"Client had an error: {exception.Message}");
            }
            finally
            {
                clientSocket.Shutdown(SocketShutdown.Both);
                clientSocket.Close();
            }
        }

        /// <summary>
        /// Create a message object with the content from the user
        /// </summary>
        /// <param name="contentMessage">Message received</param>
        /// <param name="userName">Username of the client who sent the message</param>
        /// <returns>A Message object (google protobuf class)</returns>
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

        /// <summary>
        /// Sends a message to all connected users
        /// </summary>
        /// <param name="message">Message to be sent</param>
        private void SendMessageToAllClients(Message message)
        {
            foreach (KeyValuePair<Socket, String> clientSocketsAndAddresses in this.connectedClients)
            {
                this.SendMessageToOneClient(message, clientSocketsAndAddresses.Key);
            }
        }

        /// <summary>
        /// Send a message to only one connected user
        /// </summary>
        /// <param name="message">Message to be sent</param>
        /// <param name="clientSocket">Connected client socket</param>
        private void SendMessageToOneClient(Message message, Socket clientSocket)
        {
            byte[] messageBytes = message.ToByteArray();
            clientSocket.SendAsync(messageBytes, SocketFlags.None);
        }

        // If summaries feel redundant, I am trying my best, it's 1 am :<

        /// <summary>
        /// Checks to see if the user is host via their ip address
        /// </summary>
        /// <param name="ipAddress">Ip address of the user</param>
        /// <returns>True or False</returns>
        private bool IsHost(String ipAddress)
        {
            return ipAddress == ipEndPoint.Address.ToString();
        }

        /// <summary>
        /// Initializes the server timeout
        /// The server will close itself and the host connection if the number of connected
        /// users is < MINIMUM_CONNECTION ( which is set to 2 )
        /// The timer starts at startup and any time the connection dips lower than 2
        /// Timer is SERVER_TIMEOUT_COUNTDOWN ( which is set to 3 min / 180000 milisec)
        /// </summary>
        private void InitializeServerTimeout()
        {
            // Lock is used for thread safety
            lock (this.lockTimer)
            {
                this.serverTimeout?.Dispose();
                this.serverTimeout = new System.Threading.Timer((_) =>
                {
                    // Recheck the condition after the countdown
                    if (connectedClients.Count < MINIMUM_CONNECTIONS)
                    {
                        this.ShutDownServer();
                    }
                }, null, SERVER_TIMEOUT_COUNTDOWN, System.Threading.Timeout.Infinite);
            }
        }

        /// <summary>
        /// Closes the connection from the socket
        /// </summary>
        private void ShutDownServer()
        {
            if(this.serverSocket.Connected == true)
            {
                this.serverSocket.Shutdown(SocketShutdown.Both);
            }
            this.serverSocket.Close();
            this.isRunning = false;
        }

        /// <summary>
        /// Checks for minimum connections, if not met starts the timeout
        /// </summary>
        private void CheckForMinimumConnections()
        {
            if (this.connectedClients.Count < MINIMUM_CONNECTIONS)
            {
                this.InitializeServerTimeout();
            }
        }

        /// <summary>
        /// Getter for isRunning
        /// </summary>
        /// <returns>True or False</returns>
        public bool IsServerRunning()
        {
            return this.isRunning;
        }

        /// <summary>
        /// Gets the highest status that the user has
        /// </summary>
        /// <param name="userName">Client username</param>
        /// <returns></returns>
        private String GetHighestStatus(String userName)
        {
            switch (true)
            {
                case true when this.hostName == userName:
                    return HOST_STATUS;
                case true when this.adminUsers.ContainsKey(userName):
                    return ADMIN_STATUS;
                default:
                    return REGULAR_USER_STATUS;
            }
        }

        /// <summary>
        /// Checks if the user is allowed to change the status of the targeted user
        /// </summary>
        /// <param name="firstUserStatus">Current client status</param>
        /// <param name="secondUserStatus">Targeted client status</param>
        /// <returns>True or False</returns>
        private bool IsUserAllowedOnTargetStatusChange(String firstUserStatus, String secondUserStatus)
        {
            return (firstUserStatus == HOST_STATUS && secondUserStatus != HOST_STATUS) || (firstUserStatus == ADMIN_STATUS && secondUserStatus == REGULAR_USER_STATUS);
        }
        
        /// <summary>
        /// Find the target username from a command
        /// Commands targeting users follow the match: <username>|Status|<targetedUsername>
        /// </summary>
        /// <param name="Command">Client Command</param>
        /// <returns>Target username</returns>
        private String FindTargetedUserNameFromCommand(String Command)
        {
            int commandTargetIndex = 2;
            char commandSeparator = '|';
            String commandTarget = Command.Split(commandSeparator)[commandTargetIndex];

            int nameStartIndex = 1, nameEndIndex = commandTarget.Length - 2;
            String targetedUserName = commandTarget.Substring(nameStartIndex, nameEndIndex);

            return targetedUserName;
        }

        /// <summary>
        /// Attempts at changing a user status and sends a message with the new status to all clients
        /// </summary>
        /// <param name="command">Given command</param>
        /// <param name="targetedStatus">Mute/Admin/Kick</param>
        /// <param name="userName">Client who sent the command</param>
        /// <param name="statusDataHolder">The respected map to the targeted status</param>
        private void TryChangeStatus(String command, String targetedStatus, String userName, ConcurrentDictionary<string, bool>? statusDataHolder = null)
        {
            String targetedUserName = this.FindTargetedUserNameFromCommand(command);

            String userStatus = this.GetHighestStatus(userName);
            String targetedUserStatus = this.GetHighestStatus(targetedUserName);

            if (this.IsUserAllowedOnTargetStatusChange(userStatus, targetedUserStatus))
            {
                // Add is ignored, but if not it gets the value false
                // Update changes to the negations of the current value
                bool isStatus = statusDataHolder?.AddOrUpdate(targetedUserName, false, (key, oldValue) => !oldValue) ?? false;
                String messageContent;

                if (targetedStatus.Equals(MUTE_STATUS))
                {
                    switch (isStatus)
                    {
                        case true:
                            messageContent = $"{targetedUserName} has been muted";
                            break;
                        case false:
                            messageContent = $"{targetedUserName} has been unmuted";
                            break;
                    }
                }
                else if (targetedStatus.Equals(ADMIN_STATUS))
                {
                    switch (isStatus)
                    {
                        case true:
                            messageContent = $"{targetedUserName} has been granted admin status";
                            break;
                        case false:
                            messageContent = $"{targetedUserName} has been removed from admin status";
                            break;
                    }
                }
                else
                {
                    messageContent = $"{targetedUserName} has been kicked";
                }
                this.SendMessageToAllClients(CreateMessage(messageContent, userName));
            }
        }

        /// <summary>
        /// Removes all knowledge of the client from the server
        /// </summary>
        /// <param name="clientSocket">Client socket</param>
        /// <param name="userName">Client username</param>
        /// <param name="ipAddress">Client ip address</param>
        private void RemoveClientInformation(Socket clientSocket, String userName, String ipAddress)
        {
            this.addressesAndUserNames.TryRemove(ipAddress, out _);
            this.connectedClients.TryRemove(clientSocket, out _);
            this.adminUsers.TryRemove(userName, out _);
            this.mutedUsers.TryRemove(userName, out _);
        }
    }
}
