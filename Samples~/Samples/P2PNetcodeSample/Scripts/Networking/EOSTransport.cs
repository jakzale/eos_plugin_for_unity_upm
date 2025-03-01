﻿/*
* Copyright (c) 2021 PlayEveryWare
* 
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
* 
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
* 
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*/

//#define EOS_TRANSPORT_DEBUG

#if COM_UNITY_MODULE_NETCODE

//namespace Netcode.Transports.EOSP2P
namespace PlayEveryWare.EpicOnlineServices.Samples.Network
{
    using System;
    using UnityEngine;
    using Unity.Netcode;
    using Epic.OnlineServices;
    using System.Collections.Generic;

    public class EOSTransport : NetworkTransport
    {
        // EOS Data
        private EOSTransportManager P2PManager;
        private string P2PSocketName = "EOSP2PTransport";

        // True post-initialization and pre-shutdown, else false
        private bool IsInitialized = false;

        // Our local EOS UserId (should always be valid after successful initialization)
        private ProductUserId OurUserId { get => LocalUserIdOverride ?? EOSManager.Instance?.GetProductUserId(); }

        /// <summary>
        /// Invalid ClientId
        /// </summary>
        /// <value><c>-1</c></value>
        public const ulong InvalidClientId = ulong.MaxValue;

        // Client ID Maps (locally persistent)
        private ulong NextTransportId = 1; // (ServerClientId is 0, so we start at 1)
        private Dictionary<ulong, ProductUserId> TransportIdToUserId = null;
        private Dictionary<ProductUserId, ulong> UserIdToTransportId = null;


        // True if we're the Server, else we're a Client
        private bool IsServer = false;

        /// <summary>
        /// UserID of the Server host to connect to on StartClient.
        /// This is required to be set by the user before calling StartClient
        /// </summary>
        public ProductUserId ServerUserIdToConnectTo = null;

        // Locked in after calling StartClient to avoid changing unexpectedly
        private ProductUserId ServerUserId = null;          

        // Override local user id for testing multiple clients at once
        public ProductUserId LocalUserIdOverride = null;

#if UNITY_EDITOR
        //editor field to input a PUID to connect to(ease of access when using editor buttons in the network manager
        public String ServerUserIdToConnectToInput = null;

        //String of the current server PUID for copying purposes
        public string ServerUserIDForCopying = null;
#endif
        /// <summary>
        /// A constant `clientId` that represents the server.
        /// When this value is found in methods such as `Send`, it should be 
        /// treated as a placeholder that means "the server".
        /// 
        /// While most things inside EOSTransport have some nuanced difference
        /// between a 'client id' and a 'transport id', this ServerClientId is
        /// a special constant value. Both the client id and transport id of
        /// the server will always be this value.
        /// </summary>
        public override ulong ServerClientId => 0;

        /// <summary>
        /// Gets a value indicating whether this <see cref="T:NetworkTransport"/> is supported in the current runtime context.
        /// This is used by multiplex adapters.
        /// </summary>
        /// <value><c>true</c> if is supported; otherwise, <c>false</c>.</value>
        public override bool IsSupported => true; // TODO: Do an EOS SDK platform support check? (eg. Is PC?) Not super critical right now.

        // Cached User Connected/Disconnected Events
        // UserID, ClientID, True if Connected event else Disconnected event
        private Queue<Tuple<ProductUserId, ulong, bool>> ConnectedDisconnectedUserEvents = null;

        [System.Diagnostics.Conditional("EOS_TRANSPORT_DEBUG")]
        private void Log(string msg)
        {
            Debug.Log(msg);
        }

        [System.Diagnostics.Conditional("EOS_TRANSPORT_DEBUG")]
        private void LogWarning(string msg)
        {
            Debug.LogWarning(msg);
        }

        [System.Diagnostics.Conditional("EOS_TRANSPORT_DEBUG")]
        private void LogError(string msg)
        {
            Debug.LogError(msg);
        }

        /// <summary>
        /// Send a payload to the specified clientId, data and channelName.
        /// </summary>
        /// <param name="clientId">
        /// The transport id to send to.
        /// 
        /// This function maintains the parameter name 'clientId' because of its
        /// base class, but this should be provided an associated transport id,
        /// not the client's client id.
        /// </param>
        /// <param name="payload">The data to send.</param>
        /// <param name="networkDelivery">The delivery type (QoS) to send data with.</param>
        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            Debug.Assert(IsInitialized);

            ProductUserId userId = GetUserId(clientId);

            Log($"EOSP2PTransport.Send: [ClientId='{clientId}', UserId='{userId}', PayloadBytes='{payload.Count}', SendTimeSec='{Time.realtimeSinceStartup}']");

            Epic.OnlineServices.P2P.PacketReliability reliability = Epic.OnlineServices.P2P.PacketReliability.ReliableOrdered;
            if (networkDelivery == NetworkDelivery.Unreliable)
            {
                reliability = Epic.OnlineServices.P2P.PacketReliability.UnreliableUnordered;
            }
            else if (networkDelivery == NetworkDelivery.Reliable)
            {
                reliability = Epic.OnlineServices.P2P.PacketReliability.ReliableUnordered;
            }
            else if(networkDelivery == NetworkDelivery.ReliableFragmentedSequenced || networkDelivery == NetworkDelivery.ReliableSequenced)
            {
                reliability = Epic.OnlineServices.P2P.PacketReliability.ReliableOrdered;
            }

            if (payload.Count > EOSTransportManager.MaxPacketSize)
            {
                if (reliability != Epic.OnlineServices.P2P.PacketReliability.ReliableOrdered)
                {
                    LogError($"EOSP2PTransport.Send: Unable to send payload - The payload size ({payload.Count} bytes) exceeds the maxmimum packet size supported by EOS P2P ({EOSTransportManager.MaxPacketSize} bytes).");
                    return;
                }
            }

            P2PManager.SendPacket(userId, P2PSocketName, payload, 0, false, reliability);
        }

        /// <summary>
        /// Polls for incoming events, with an extra output parameter to report the precise time the event was received.
        /// </summary>
        /// <param name="clientId">
        /// The transportId this event is for.
        /// 
        /// This function maintains the parameter name 'clientId' because of its
        /// base class, but this should be provided an associated transport id,
        /// not the client's client id.
        /// </param>
        /// <param name="payload">The incoming data payload.</param>
        /// <param name="receiveTime">The time the event was received, as reported by Time.realtimeSinceStartup.</param>
        /// <returns>Returns the event type.</returns>
        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            Debug.Assert(IsInitialized);

            // Any users connected or disconnected?
            if (ConnectedDisconnectedUserEvents.Count > 0)
            {
                Tuple<ProductUserId, ulong, bool> evnt = ConnectedDisconnectedUserEvents.Dequeue();
                ProductUserId evntUserId = evnt.Item1;
                ulong evntClientId = evnt.Item2;
                bool evntIsConnectionEvent = evnt.Item3;

                clientId = evntClientId;
                
                payload = new ArraySegment<byte>();
                receiveTime = Time.realtimeSinceStartup;
                NetworkEvent networkEventType = evntIsConnectionEvent ? NetworkEvent.Connect : NetworkEvent.Disconnect;
                Log($"EOSP2PTransport.PollEvent: [{networkEventType}, ClientId='{clientId}', UserId='{evntUserId}', PayloadBytes='{payload.Count}', RecvTimeSec='{receiveTime}']");
                return networkEventType;
            }

            // Any packets to be received?
            if (P2PManager.TryReceivePacket(out ProductUserId userId, out string socketName, out byte channel, out byte[] packet))
            {
                Debug.Assert(socketName == P2PSocketName);

                clientId = GetTransportId(userId);
                payload = new ArraySegment<byte>(packet);
                receiveTime = Time.realtimeSinceStartup;
                Log($"EOSP2PTransport.PollEvent: [{NetworkEvent.Data}, ClientId='{clientId}', UserId='{userId}', PayloadBytes='{payload.Count}', RecvTimeSec='{receiveTime}']");
                return NetworkEvent.Data;
            }

            // Otherwise, nothing to report
            clientId = InvalidClientId;
            payload = new ArraySegment<byte>();
            receiveTime = 0;
            Log("EOSP2PTransport.PollEvent: []");
            return NetworkEvent.Nothing;
        }

        /// <summary>
        /// Starts up in client mode and connects to the server.
        /// </summary>
        public override bool StartClient()
        {
#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(ServerUserIdToConnectToInput))
            {
                ServerUserIdToConnectTo = ProductUserId.FromString(ServerUserIdToConnectToInput);
            }
#endif
            if (ServerUserIdToConnectTo == null)
            {
                Log("EOSP2PTransport.StartClient: No ServerUserIDToConnectTo set!");
                return false;
            }

            Debug.Assert(IsInitialized);

            bool result;

            // Set client mode
            IsServer = false;

            // User provided a valid ServerUserIdToConnectTo?
            if (result = (ServerUserIdToConnectTo != null && ServerUserIdToConnectTo.IsValid()))
            {
                // Store it in ServerUserId so it can't be changed after start up
                ServerUserId = ServerUserIdToConnectTo;

                // Attempt to connect to the server hosted by ServerUserId - was the request successfully initiated?
                if (result = P2PManager.OpenConnection(ServerUserId, P2PSocketName))
                {
                    Log($"EOSP2PTransport.StartClient: Successful Client start up - REQUESTED outgoing '{P2PSocketName}' socket connection with Server UserId Server UserId='{ServerUserId}'.");
                    result = true;
                }
                else
                {
                    LogError($"EOSP2PTransport.StartClient: Failed Client start up - Unable to initiate a connect request with Server UserId='{ServerUserId}'.");
                }
            }
            else
            {
                LogError("EOSP2PTransport.StartClient: Failed Client start up - 'ServerUserIdToConnectTo' is null or invalid."
                    + " Please set a valid EOS ProductUserId of the Server host this Client should try connecting to in the 'ServerUserIdToConnectTo' property before calling StartClient"
                    + $" (ServerUserIdToConnectTo='{ServerUserIdToConnectTo}').");
            }

            return result;
        }

        /// <summary>
        /// Starts up in server mode and begins listening for incoming clients.
        /// </summary>
        public override bool StartServer()
        {
            Debug.Assert(IsInitialized);
            Log($"EOSP2PTransport.StartServer: Entering Server mode with EOS UserId='{OurUserId}'.");
#if UNITY_EDITOR
            ServerUserIDForCopying = OurUserId.ToString();
#endif
            // Set server mode
            IsServer = true;

            // Success
            return true;
        }

        /// <summary>
        /// Disconnects a client from the server.
        /// </summary>
        /// <param name="clientId">
        /// The transport id to disconnect.
        /// 
        /// This should be the transport id of the target user, not their client
        /// id in Network Manager.
        /// </param>
        public override void DisconnectRemoteClient(ulong clientId)
        {
            Debug.Assert(IsInitialized);
            Debug.Assert(IsServer);

            ProductUserId userId = GetUserId(clientId);

            Log($"EOSP2PTransport.DisconnectRemoteClient: Disconnecting ClientId='{clientId}' (UserId='{userId}') from our Server.");
            P2PManager.CloseConnection(userId, P2PSocketName, true);
        }

        /// <summary>
        /// Disconnects the local client from the server.
        /// </summary>
        public override void DisconnectLocalClient()
        {
            Debug.Assert(IsInitialized);
            Debug.Assert(IsServer == false);

            Log($"EOSP2PTransport.DisconnectLocalClient: Disconnecting our Client from the Server (UserId='{ServerUserId}').");
            P2PManager.CloseConnection(ServerUserId, P2PSocketName, true);
        }

        /// <summary>
        /// Gets the round trip time for a specific client.
        /// This method is optional, and not currently implemented in this case.
        /// </summary>
        /// <param name="clientId">
        /// The transport id to get the RTT from.
        /// 
        /// This should be the transport id of the target user, not their client
        /// id in Network Manager.
        /// </param>
        /// <returns><c>0</c></returns>
        public override ulong GetCurrentRtt(ulong clientId)
        {
            Debug.Assert(IsInitialized);
            /*
             * RTT can be calculated by subtracting the time at which
             * DateTime.Now is sent from the time at which it is returned from
             * the opponent.
             *
             * You can implement it by sending DateTime in your request, and
             * subsequently subtracting that value from DateTime.Now when the
             * response is received.
             *
             * It is not currently implemented due to the complexity it would
             * add to the samples.
             */
            return 0;
        }

        /// <summary>
        /// Shuts down the transport.
        /// </summary>
        public override void Shutdown()
        {
            Debug.Assert(IsInitialized);
            Log("EOSP2PTransport.Shutdown: Shutting down Epic Online Services Peer-2-Peer NetworkTransport.");
            IsInitialized = false;

            // Shutdown EOS Peer-2-Peer Manager
            if (P2PManager != null)
            {
                P2PManager.Shutdown();
                P2PManager.OnIncomingConnectionRequestedCb = null;
                P2PManager.OnConnectionOpenedCb = null;
                P2PManager.OnConnectionClosedCb = null;
                P2PManager = null;
            }

            // Clear user connects/disconnects event cache
            ConnectedDisconnectedUserEvents = null;

            // Clear ID maps
            TransportIdToUserId = null;
            UserIdToTransportId = null;

            // Clear Server UserId target
            ServerUserId = null;
        }

        /// <summary>
        /// Initializes the transport.
        /// </summary>
        public override void Initialize(NetworkManager networkManager)
        {
            Debug.Assert(IsInitialized == false);
            Log("EOSP2PTransport.Initialize: Initializing Epic Online Services Peer-2-Peer NetworkTransport.");

            // EOSManager should already be initialized and exist by this point
            if (EOSManager.Instance == null)
            {
                LogError("EOSP2PTransport.Initialize: Unable to initialize - EOSManager singleton is null (has the EOSManager component been added to an object in your initial scene?)");
                return;
            }

            // Create ID maps
            NextTransportId = 1; // Reset local client ID assignment counter
            TransportIdToUserId = new Dictionary<ulong, ProductUserId>();
            UserIdToTransportId = new Dictionary<ProductUserId, ulong>();

            // Create user connects/disconnects event cache
            ConnectedDisconnectedUserEvents = new Queue<Tuple<ProductUserId, ulong, bool>>();

            // Initialize EOS Peer-2-Peer Manager
            Debug.Assert(P2PManager == null);

            if (LocalUserIdOverride?.IsValid() == true)
            {
                P2PManager = new EOSTransportManager(LocalUserIdOverride);
            }
            else
            {
                P2PManager = EOSManager.Instance.GetOrCreateManager<EOSTransportManager>();
            }
            
            P2PManager.OnIncomingConnectionRequestedCb = OnIncomingConnectionRequestedCallback;
            P2PManager.OnConnectionOpenedCb = OnConnectionOpenedCallback;
            P2PManager.OnConnectionClosedCb = OnConnectionClosedCallback;
            if (P2PManager.Initialize() == false)
            {
                LogError("EOSP2PTransport.Initialize: Unable to initialize - EOSP2PManager failed to initialize.");
                P2PManager.OnIncomingConnectionRequestedCb = null;
                P2PManager.OnConnectionOpenedCb = null;
                P2PManager.OnConnectionClosedCb = null;
                P2PManager = null;
                return;
            }

            IsInitialized = true;

        }

        /// <summary>
        /// Leaves any existing lobbies, shuts down the network manager, and logs the local user out of EOS.
        /// </summary>
        /*public void ForceLogout()
        {
            EOSManager.Instance.GetOrCreateManager<EOSLobbyManager>().LeaveLobby(null);

            NetworkManager.Singleton.Shutdown();

            EOSManager.Instance.StartLogout(EOSManager.Instance.GetLocalUserId(), (LogoutCallbackInfo data) =>
            {
                if (data.ResultCode == Result.Success)
                {
                    Log("Logout Successful. [" + data.ResultCode + "]");
                    GameStateManager.ChangeScene("MainMenu", false);
                }
            });
        }*/

        /// <summary>
        /// Called when a connection is originally requested by a remote peer (opened remotely but not yet locally).
        /// </summary>
        /// <param name="userId">The id of the remote peer requesting a connection.</param>
        /// <param name="socketName">The name of the socket the requested connection is on.</param>
        public void OnIncomingConnectionRequestedCallback(ProductUserId userId, string socketName)
        {
            Debug.Assert(IsInitialized);

            // For now if we're a server we'll just accept all incoming requests trying to establish an "EOSP2PTransport" socket connection.
            // TODO: Add more intelligent logic here (limiting the maximum number of players on a server, etc.)
            if (IsServer && socketName == P2PSocketName)
            {
                // Accept connection request
                Log($"EOSP2PTransport.OnIncomingConnectionRequestedCallback: ACCEPTING incoming '{socketName}' socket connection request from UserId='{userId}'.");
                P2PManager.OpenConnection(userId, socketName);
            }
            else
            {
                // Reject connection request
                Log($"EOSP2PTransport.OnIncomingConnectionRequestedCallback: REJECTING incoming '{socketName}' socket connection request from UserId='{userId}'.");
                P2PManager.CloseConnection(userId, socketName);
            }
        }

        /// <summary>
        /// Called immediately after a remote peer connection becomes fully opened (opened both locally and remotely).
        /// </summary>
        /// <param name="userId">The id of the remote peer that has been connected to.</param>
        /// <param name="socketName">The name of the socket the newly opened connection is on.</param>
        public void OnConnectionOpenedCallback(ProductUserId userId, string socketName)
        {
            Debug.Assert(IsInitialized);

            Log($"EOSP2PTransport.OnConnectionOpenedCallback: '{socketName}' socket connection OPENED with UserId='{userId}'.");
            if (socketName == P2PSocketName)
            {
                if (IsServer)
                {
                    // We don't have this client in our map yet? (ie. We haven't seen them before)
                    if (UserIdToTransportId.ContainsKey(userId) == false)
                    {
                        // Add client ID mapping
                        ulong newClientId = NextTransportId++; // Generate new client ID (locally unique, incremental)
                        TransportIdToUserId.Add(newClientId, userId);
                        UserIdToTransportId.Add(userId, newClientId);
                    }
                }

                // Get mapped client ID
                ulong clientId = GetTransportId(userId);

                // Cache user connection event, will be returned in a later PollEvent call
                ConnectedDisconnectedUserEvents.Enqueue(new Tuple<ProductUserId, ulong, bool>(userId, clientId, true));
            }
        }

        // Called immediately before a fully opened remote peer connection is closed (closed either locally or remotely).
        // Guaranteed to be called only if OnConnectionOpenedCallback was attempted to be called earlier for any given connection.

        /// <summary>
        /// Called immediately before a fully opened remote peer connection is closed (closed either locally or remotely).
        /// Guaranteed to be called only if OnConnectionOpenedCallback was attempted to be called earlier for any given connection.
        /// </summary>
        /// <param name="userId">The id of the remote peer that will be disconnected from.</param>
        /// <param name="socketName">The name of the socket to close the connection on.</param>
        public void OnConnectionClosedCallback(ProductUserId userId, string socketName)
        {
            Debug.Assert(IsInitialized);

            Log($"EOSP2PTransport.OnConnectionClosedCallback: '{socketName}' socket connection CLOSED with UserId='{userId}'.");
            if (socketName == P2PSocketName)
            {
                // We're the Server?
                if (IsServer)
                {
                    // We should have seen this client before in a prior call to OnConnectionOpenedCallback
                    Debug.Assert(UserIdToTransportId.ContainsKey(userId) == true);
                }

                // Get mapped client ID
                ulong clientId = GetTransportId(userId);

                // NOTE: For simplicity of event processing order and ID lookups we will simply allow the client ID map
                // to continually grow as we don't expect to receive an unreasonable number (>10k) of unique
                // user connections during the lifetime of the host application.

                // if (IsServer)
                // {
                //   // Remove client ID mapping
                //   ulong clientId = UserIdToClientId[userId];
                //   ClientIdToUserId.Remove(clientId);
                //   UserIdToClientId.Remove(userId);
                // }

                // Cache user disconnection event, will be returned in a later PollEvent call
                ConnectedDisconnectedUserEvents.Enqueue(new Tuple<ProductUserId, ulong, bool>(userId, clientId, false));
            }
        }

        /// <summary>
        /// Gets the ProductUserId for an associated transportId.
        /// 
        /// As only a Server is able to know about clients, calling this 
        /// function from a client will always return <see cref="ServerUserId"/>.
        /// </summary>
        /// <param name="transportId">
        /// The transport id of the user to look up.
        /// Note that this is not the same as the "client id" inside 
        /// NetworkManager. <see cref="GetTransportId(ProductUserId)"/>
        /// </param>
        /// <returns>The product user associated with the transport.</returns>
        public ProductUserId GetUserId(ulong transportId)
        {
            Debug.Assert(IsInitialized);

            // We're a Client?
            if (IsServer == false)
            {
                Debug.AssertFormat(transportId == ServerClientId, "EOSP2PTransport.GetUserId: Unexpected ClientId='{0}' given - We're a Client so we should only be dealing with the Server by definition (Server ClientId='{1}').",
                                   transportId, ServerClientId);
                return ServerUserId;
            }
            else
            {
                return TransportIdToUserId[transportId];
            }
        }

        /// <summary>
        /// Gets the TransportId for an associated ProductUserId.
        /// 
        /// As only a Server is able to know about clients, calling this 
        /// function from a client will always return 
        /// <see cref="ServerClientId"/>.
        /// 
        /// The term 'client id' and 'transport id' are related, but not exactly
        /// the same. When a new client connects to a host,
        /// <see cref="NetworkManager"/> assigns that user a client id. The
        /// assigned <see cref="NetworkTransport"/> (which is this class) is
        /// responsible for identifying when a user joins, and assigning them
        /// a transport id. NetworkManager and EOSTransport independently
        /// assign these ids, and while they might coincide they are not
        /// guaranteed to be the same. For example; if a user starts a client,
        /// they might have a transport id that is the same as their client id.
        /// But if they leave and return, EOSTransport will reuse the same
        /// transport id, while NetworkManager will use a new client id.
        /// 
        /// Most NetworkManager functions take in a client id. If it needs to
        /// do something relating to the NetworkTransport, it will use an
        /// internal lookup table to translate from the client id to transport
        /// id, and then run the NetworkTransport function. Whenever 
        /// NetworkManager calls into  NetworkTransport, it is always going to 
        /// provide a transport id, even if the parameter name in the transport
        /// is 'clientId'.
        /// 
        /// In order to manage clients, this function can be used to map from
        /// ProductUserId to transport id. That transport id can then be used
        /// to call public functions inside EOSTransport, which will usually
        /// ripple up to NetworkManager to handle client id information. When
        /// EOSTransport calls NetworkManager to run code, the NetworkManager
        /// will usually translate the transport id back in to a client id.
        /// </summary>
        /// <param name="userId">The EOS Product User to lookup.</param>
        /// <returns>The transport id associated with the user.</returns>
        public ulong GetTransportId(ProductUserId userId)
        {
            Debug.Assert(IsInitialized);

            // We're a Client?
            if (IsServer == false)
            {
                Debug.AssertFormat(userId == ServerUserId, "EOSP2PTransport.GetClientId: Unexpected UserId='{0}' given - We're a Client so we should only be dealing with the Server by definition (Server UserId='{1}').",
                                   userId, ServerUserId);
                return ServerClientId;
            }
            else
            {
                return UserIdToTransportId[userId];
            }
        }
    }
}

#endif