# Copyright 2017 Intel Corporation
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
# ------------------------------------------------------------------------------
import logging
import copy
import time
import random
import os
import binascii
from threading import RLock
from functools import partial
from collections import namedtuple
from enum import Enum

from sawtooth_validator.concurrent.thread import InstrumentedThread
from sawtooth_validator.protobuf.network_pb2 import DisconnectMessage
from sawtooth_validator.protobuf.network_pb2 import GossipMessage
from sawtooth_validator.protobuf.network_pb2 import GossipBatchByBatchIdRequest
from sawtooth_validator.protobuf.network_pb2 import \
    GossipBatchByTransactionIdRequest
from sawtooth_validator.protobuf.network_pb2 import GossipBlockRequest
from sawtooth_validator.protobuf import validator_pb2
from sawtooth_validator.protobuf.network_pb2 import PeerRegisterRequest
from sawtooth_validator.protobuf.network_pb2 import PeerUnregisterRequest
from sawtooth_validator.protobuf.network_pb2 import GetPeersRequest
from sawtooth_validator.protobuf.network_pb2 import GetPeersResponse
from sawtooth_validator.protobuf.network_pb2 import NetworkAcknowledgement
from sawtooth_validator.exceptions import PeeringException

LOGGER = logging.getLogger(__name__)


class PeerStatus(Enum):
    CLOSED = 1
    TEMP = 2
    PEER = 3


class EndpointStatus(Enum):
    # Endpoint will be used for peering
    PEERING = 1
    # Endpoint will be used to request peers
    TOPOLOGY = 2


EndpointInfo = namedtuple('EndpointInfo',
                          ['status', 'time', "retry_threshold"])

StaticPeerInfo = namedtuple('StaticPeerInfo',
                            ['time', 'retry_threshold', 'count'])

INITIAL_RETRY_FREQUENCY = 10
MAXIMUM_RETRY_FREQUENCY = 300

MAXIMUM_STATIC_RETRY_FREQUENCY = 3600
MAXIMUM_STATIC_RETRIES = 24

TIME_TO_LIVE = 3

# This is the protocol version number.  It should only be incremented when
# there are changes to the network protocols, as well as only once per
# release.
NETWORK_PROTOCOL_VERSION = 1


class Gossip(object):
    def __init__(self, network,
                 settings_cache,
                 current_chain_head_func,
                 current_root_func,
                 endpoint=None,
                 peering_mode='static',
                 initial_seed_endpoints=None,
                 initial_peer_endpoints=None,
                 minimum_peer_connectivity=3,
                 maximum_peer_connectivity=10,
                 topology_check_frequency=1
                 ):
        """Constructor for the Gossip object. Gossip defines the
        overlay network above the lower level networking classes.

        Args:
            network (networking.Interconnect): Provides inbound and
                outbound network connections.
            settings_cache (state.SettingsCache): A cache for on chain
                settings.
            current_chain_head_func (function): returns the current chain head.
            current_root_func (function): returns the current state root hash
                for the current chain root.
            endpoint (str): The publically accessible zmq-style uri
                endpoint for this validator.
            peering_mode (str): The type of peering approach. Either 'static'
                or 'dynamic'. In 'static' mode, no attempted topology
                buildout occurs -- the validator only attempts to initiate
                peering connections with endpoints specified in the
                peer_list. In 'dynamic' mode, the validator will first
                attempt to initiate peering connections with endpoints
                specified in the peer_list and then attempt to do a
                topology buildout starting with peer lists obtained from
                endpoints in the seeds_list. In either mode, the validator
                will accept incoming peer requests up to max_peers.
            initial_seed_endpoints ([str]): A list of initial endpoints
                to attempt to connect and gather initial topology buildout
                information from. These are specified as zmq-compatible
                URIs (e.g. tcp://hostname:port).
            initial_peer_endpoints ([str]): A list of initial peer endpoints
                to attempt to connect and peer with. These are specified
                as zmq-compatible URIs (e.g. tcp://hostname:port).
            minimum_peer_connectivity (int): If the number of connected
                peers is below this threshold, the topology builder will
                continue to attempt to identify new candidate peers to
                connect with.
            maximum_peer_connectivity (int): The validator will reject
                new peer requests if the number of connected peers
                reaches this threshold.
            topology_check_frequency (int): The time in seconds between
                topology update checks.
        """
        self._peering_mode = peering_mode
        self._lock = RLock()
        self._network = network
        self._endpoint = endpoint
        self._initial_seed_endpoints = initial_seed_endpoints \
            if initial_seed_endpoints else []
        self._initial_peer_endpoints = initial_peer_endpoints \
            if initial_peer_endpoints else []
        self._minimum_peer_connectivity = minimum_peer_connectivity
        self._maximum_peer_connectivity = maximum_peer_connectivity
        self._topology_check_frequency = topology_check_frequency
        self._settings_cache = settings_cache

        self._current_chain_head_func = current_chain_head_func
        self._current_root_func = current_root_func

        self._topology = None
        self._peers = {}

    def send_peers(self, connection_id):
        """Sends a message containing our peers to the
        connection identified by connection_id.

        Args:
            connection_id (str): A unique identifier which identifies an
                connection on the network server socket.
        """
        # Needs to actually be the list of advertised endpoints of
        # our peers
        peer_endpoints = list(self.get_peers_filtered().values())

        if self._endpoint:
            peer_endpoints.append(self._endpoint)
        peers_response = GetPeersResponse(peer_endpoints=peer_endpoints)
        try:
            # Send a one_way message because the connection will be closed
            # if this is a temp connection.
            self._network.send(
                validator_pb2.Message.GOSSIP_GET_PEERS_RESPONSE,
                peers_response.SerializeToString(),
                connection_id,
                one_way=True)
        except ValueError:
            LOGGER.debug("Connection disconnected: %s", connection_id)

    def add_candidate_peer_endpoints(self, peer_endpoints):
        """Adds candidate endpoints to the list of endpoints to
        attempt to peer with.

        Args:
            peer_endpoints ([str]): A list of public uri's which the
                validator can attempt to peer with.
        """
        if self._topology:
            self._topology.add_candidate_peer_endpoints(peer_endpoints)
        else:
            LOGGER.debug("Could not add peer endpoints to topology. "
                         "ConnectionManager does not exist.")

    def _get_outbound_connection(self, endpoint):
        return self._network._get_outbound_connection(endpoint)

    def _get_inbound_connection(self, endpoint):
        return self._network._get_inbound_connection(endpoint)

    def get_peers(self):
        """Returns a copy of the gossip peers.
        """
        with self._lock:
            return copy.copy(self._peers)

    def get_peers_filtered(self):
        """Returns a copy of the gossip peers.
        """
        peers = self.get_peers()
        return { k: v for k, v in peers.items() if self._topology.get_connection_status(k) == PeerStatus.PEER }

    def register_peer(self, connection_id, endpoint, status=PeerStatus.PEER):
        """Registers a connected connection_id.

        Args:
            connection_id (str): A unique identifier which identifies an
                connection on the network server socket.
            endpoint (str): The publically reachable endpoint of the new
                peer
        """
        error = False
        with self._lock:
            if connection_id in self.get_peers_filtered():
                raise PeeringException("Already peered with %s", endpoint)
            abandoned = []
            for conn_id in self._peers:
                if self._peers[conn_id] == endpoint and self._topology.get_connection_status(conn_id) == PeerStatus.PEER:
                    abandoned.append(conn_id)
            if len(self._peers) < self._maximum_peer_connectivity or status == PeerStatus.TEMP:
                self._peers[connection_id] = endpoint
                self._topology.set_connection_status(connection_id,
                                                     status)
                LOGGER.debug("Added connection_id %s with endpoint %s, "
                             "connected identities are now %s",
                             connection_id, endpoint, self._peers)
            else:
                error = True
        for conn_id in abandoned:
            self.unregister_peer(conn_id)
            self._topology._remove_temporary_connection(conn_id)
        if error:
            raise PeeringException(
                "At maximum configured number of peers: {} "
                "Rejecting peering request from {}.".format(
                    self._maximum_peer_connectivity,
                    endpoint))

    def _register_temp_peer(self, connection_id, endpoint):
        self.register_peer(connection_id, endpoint, PeerStatus.TEMP)

    def unregister_peer(self, connection_id):
        """Removes a connection_id from the registry.

        Args:
            connection_id (str): A unique identifier which identifies an
                connection on the network server socket.
        """
        with self._lock:
            if connection_id in self._peers:
                del self._peers[connection_id]
                LOGGER.debug("Removed connection_id %s, "
                             "connected identities are now %s",
                             connection_id, self._peers)
                self._topology.set_connection_status(connection_id,
                                                     PeerStatus.TEMP)
            else:
                LOGGER.debug("Connection unregister failed as connection "
                             "was not registered: %s",
                             connection_id)

    def get_time_to_live(self):
        time_to_live = \
            self._settings_cache.get_setting(
                "sawtooth.gossip.time_to_live",
                self._current_root_func(),
                default_value=TIME_TO_LIVE
            )
        return int(time_to_live)

    def broadcast_block(self, block, exclude=None):
        time_to_live = self.get_time_to_live()
        gossip_message = GossipMessage(
            content_type=GossipMessage.BLOCK,
            content=block.SerializeToString(),
            time_to_live=time_to_live)

        self.broadcast(
            gossip_message, validator_pb2.Message.GOSSIP_MESSAGE, exclude)

    def broadcast_block_request(self, block_id):
        time_to_live = self.get_time_to_live()
        block_request = GossipBlockRequest(
            block_id=block_id,
            nonce=binascii.b2a_hex(os.urandom(16)),
            time_to_live=time_to_live)
        self.broadcast(block_request,
                       validator_pb2.Message.GOSSIP_BLOCK_REQUEST)

    def send_block_request(self, block_id, connection_id):
        time_to_live = self.get_time_to_live()
        block_request = GossipBlockRequest(
            block_id=block_id,
            nonce=binascii.b2a_hex(os.urandom(16)),
            time_to_live=time_to_live)
        self.send(validator_pb2.Message.GOSSIP_BLOCK_REQUEST,
                  block_request.SerializeToString(),
                  connection_id,
                  one_way=True)

    def broadcast_batch(self, batch, exclude=None):
        time_to_live = self.get_time_to_live()
        gossip_message = GossipMessage(
            content_type=GossipMessage.BATCH,
            content=batch.SerializeToString(),
            time_to_live=time_to_live)

        self.broadcast(
            gossip_message, validator_pb2.Message.GOSSIP_MESSAGE, exclude)

    def broadcast_batch_by_transaction_id_request(self, transaction_ids):
        time_to_live = self.get_time_to_live()
        batch_request = GossipBatchByTransactionIdRequest(
            ids=transaction_ids,
            nonce=binascii.b2a_hex(os.urandom(16)),
            time_to_live=time_to_live)
        self.broadcast(
            batch_request,
            validator_pb2.Message.GOSSIP_BATCH_BY_TRANSACTION_ID_REQUEST)

    def broadcast_batch_by_batch_id_request(self, batch_id):
        time_to_live = self.get_time_to_live()
        batch_request = GossipBatchByBatchIdRequest(
            id=batch_id,
            nonce=binascii.b2a_hex(os.urandom(16)),
            time_to_live=time_to_live)
        self.broadcast(
            batch_request,
            validator_pb2.Message.GOSSIP_BATCH_BY_BATCH_ID_REQUEST)

    def send(self, message_type, message, connection_id, one_way=False):
        """Sends a message via the network.

        Args:
            message_type (str): The type of the message.
            message (bytes): The message to be sent.
            connection_id (str): The connection to send it to.
        """
        try:
            self._network.send(message_type, message, connection_id,
                               one_way=one_way)
        except ValueError:
            LOGGER.debug("Connection %s is no longer valid. "
                         "Removing from list of peers.",
                         connection_id)
            with self._lock:
                if connection_id in self._peers:
                    del self._peers[connection_id]
            self._topology.remove_connection_status(connection_id)

    def broadcast(self, gossip_message, message_type, exclude=None):
        """Broadcast gossip messages.

        Broadcast the message to all peers unless they are in the excluded
        list.

        Args:
            gossip_message: The message to be broadcast.
            message_type: Type of the message.
            exclude: A list of connection_ids that should be excluded from this
                broadcast.
        """
        sendto = []
        with self._lock:
            if exclude is None:
                exclude = []
            for connection_id in self._peers:
                if connection_id not in exclude:
                    sendto.append(connection_id)
            for connection_id in sendto:
                self.send(
                    message_type,
                    gossip_message.SerializeToString(),
                    connection_id,
                    one_way=True)

    def connect_success(self, connection_id):
        """
        Notify topology that a connection has been properly authorized

        Args:
            connection_id: The connection id for the authorized connection.

        """
        if self._topology:
            self._topology.connect_success(connection_id)

    def remove_temp_endpoint(self, endpoint):
        """
        Remove temporary endpoints that never finished authorization.

        Args:
            endpoint: The endpoint that is not authorized to connect to the
                network.
        """
        if self._topology:
            self._topology.remove_temp_endpoint(endpoint)

    def start(self):
        self._topology = ConnectionManager(
            gossip=self,
            network=self._network,
            endpoint=self._endpoint,
            current_chain_head_func=self._current_chain_head_func,
            initial_peer_endpoints=self._initial_peer_endpoints,
            initial_seed_endpoints=self._initial_seed_endpoints,
            peering_mode=self._peering_mode,
            min_peers=self._minimum_peer_connectivity,
            max_peers=self._maximum_peer_connectivity,
            check_frequency=self._topology_check_frequency)

        self._topology.start()

    def stop(self):
        for peer in self.get_peers():
            request = PeerUnregisterRequest()
            try:
                self._network.send(validator_pb2.Message.GOSSIP_UNREGISTER,
                                   request.SerializeToString(),
                                   peer)
            except ValueError:
                pass
        if self._topology:
            self._topology.stop()


class ConnectionManager(InstrumentedThread):
    def __init__(self, gossip, network, endpoint,
                 current_chain_head_func,
                 initial_peer_endpoints, initial_seed_endpoints,
                 peering_mode, min_peers=3, max_peers=10,
                 check_frequency=1):
        """Constructor for the ConnectionManager class.

        Args:
            gossip (gossip.Gossip): The gossip overlay network.
            network (network.Interconnect): The underlying network.
            endpoint (str): A zmq-style endpoint uri representing
                this validator's publically reachable endpoint.
            current_chain_head_func (function): Returns the current chain head.
            initial_peer_endpoints ([str]): A list of static peers
                to attempt to connect and peer with.
            initial_seed_endpoints ([str]): A list of endpoints to
                connect to and get candidate peer lists to attempt
                to reach min_peers threshold.
            peering_mode (str): Either 'static' or 'dynamic'. 'static'
                only connects to peers in initial_peer_endpoints.
                'dynamic' connects to peers in initial_peer_endpoints
                and gets candidate peer lists from initial_seed_endpoints.
            min_peers (int): The minimum number of peers required to stop
                attempting candidate connections.
            max_peers (int): The maximum number of active peer connections
                to allow.
            check_frequency (int): How often to attempt dynamic connectivity.
        """
        super().__init__(name="ConnectionManager")
        self._lock = RLock()
        self._stopped = False
        self._gossip = gossip
        self._network = network
        self._endpoint = endpoint
        self._current_chain_head_func = current_chain_head_func
        self._initial_peer_endpoints = initial_peer_endpoints
        self._initial_seed_endpoints = initial_seed_endpoints
        self._peering_mode = peering_mode
        self._min_peers = min_peers
        self._max_peers = max_peers
        self._check_frequency = check_frequency

        self._candidate_peer_endpoints = []
        # Seconds to wait for messages to arrive
        self._response_duration = 5
        self._connection_statuses = {}
        self._temp_endpoints = {}
        self._static_peer_status = {}
        self._connection_attempted = {}
        self._connection_cooldown = {}

    def start(self):
        # First, attempt to connect to explicit peers
        for endpoint in self._initial_peer_endpoints:
            self._static_peer_status[endpoint] = \
                StaticPeerInfo(
                    time=0,
                    retry_threshold=INITIAL_RETRY_FREQUENCY,
                    count=0)

        super().start()

    def run(self):
        has_chain_head = self._current_chain_head_func() is not None
        while not self._stopped:
            try:
                if self._peering_mode == 'dynamic':
                    self.retry_dynamic_peering()
                elif self._peering_mode == 'static':
                    self.retry_static_peering()

                # This tests for a degenerate case where the node is connected
                # to peers, but at first connection no peer had a valid chain
                # head. Keep querying connected peers until a valid chain head
                # is received.
                has_chain_head = has_chain_head or \
                    self._current_chain_head_func() is not None
                if not has_chain_head:
                    peered_connections = self._get_peered_connections()
                    if peered_connections:
                        LOGGER.debug(
                            'Have not received a chain head from peers. '
                            'Requesting from %s',
                            peered_connections)

                        self._request_chain_head(peered_connections)

                time.sleep(self._check_frequency)
            except Exception:  # pylint: disable=broad-except
                LOGGER.exception("Unhandled exception during peer refresh")

    def stop(self):
        self._stopped = True
        with self._lock:
            for connection_id in self._connection_statuses:
                try:
                    if self._connection_statuses[connection_id] == \
                            PeerStatus.CLOSED:
                        continue

                    msg = DisconnectMessage()
                    self._network.send(
                        validator_pb2.Message.NETWORK_DISCONNECT,
                        msg.SerializeToString(),
                        connection_id)
                    self._connection_statuses[connection_id] = PeerStatus.CLOSED
                except ValueError:
                    # Connection has already been disconnected.
                    pass

    def _get_peered_connections(self):
        peers = self._gossip.get_peers()

        with self._lock:
            return [conn_id for conn_id in peers
                if conn_id in self._connection_statuses and self._connection_statuses[conn_id] == PeerStatus.PEER]

    def _request_chain_head(self, peered_connections):
        """Request chain head from the given peer ids.

        Args:
            peered_connecions (:list:str): a list of peer connection ids where
                the requests will be sent.
        """
        for conn_id in peered_connections:
            self._gossip.send_block_request("HEAD", conn_id)

    def retry_dynamic_peering(self):
        self._refresh_peer_list(self._gossip.get_peers_filtered())
        peers = self._gossip.get_peers_filtered()
        peer_count = len(peers)
        if peer_count < self._min_peers:
            LOGGER.debug(
                "Number of peers (%s) below "
                "minimum peer threshold (%s). "
                "Doing topology search.",
                peer_count,
                self._min_peers)

            self._refresh_peer_list(peers)
            # Cleans out any old connections that have disconnected
            self._refresh_connection_list()
            self._check_temp_endpoints()

            peers = self._gossip.get_peers_filtered()
            peered_endpoints = list(peers.values())

            with self._lock:
                candidates = self._candidate_peer_endpoints
                if len(candidates) > 16384:
                    self._reset_candidate_peer_endpoints()
                if len(candidates) == 0:
                    candidates = self._initial_seed_endpoints

                ready_candidates = self.get_ready_candidates(peered_endpoints)
                ready_peers = []
                for candidate in ready_candidates:
                    try:
                        ready_peers.append(self._network.get_connection_id_by_endpoint(candidate))
                        self._connection_cooldown[candidate] = self._connection_cooldown[candidate] * 2
                        self._connection_attempted[candidate] = time.time()
                    except KeyError:
                        pass
                self._get_peers_of_peers(ready_peers)

                ready_candidates = self.get_ready_candidates(candidates)
                ready_candidates = list(
                    set(ready_candidates)
                    - set(peered_endpoints)
                    - set([self._endpoint]))
                for candidate in ready_candidates:
                    self._connection_cooldown[candidate] = self._connection_cooldown[candidate] * 2
                    self._connection_attempted[candidate] = time.time()
                self._get_peers_of_endpoints(peers, ready_candidates)

            # Wait for GOSSIP_GET_PEER_RESPONSE messages to arrive
            time.sleep(self._response_duration)

            with self._lock:
                unpeered_candidates = list(
                    set(self._candidate_peer_endpoints)
                    - set(peered_endpoints)
                    - set([self._endpoint]))

            ready_candidates = self.get_ready_candidates(unpeered_candidates)

            LOGGER.debug(
                "Peers are: %s. "
                "Unpeered candidates count: %s. Ready candidates count %s",
                peered_endpoints,
                len(unpeered_candidates),
                len(ready_candidates))

            if len(ready_candidates):
                candidate = random.choice(ready_candidates)
                with self._lock:
                    self._connection_cooldown[candidate] = self._connection_cooldown[candidate] * 2
                    self._connection_attempted[candidate] = time.time()
                self._attempt_to_peer_with_endpoint(candidate)

    def get_ready_candidates(self, candidates):
        ready_candidates = []
        with self._lock:
            for uri in candidates:
                attempted = self._connection_attempted.get(uri)
                if attempted:
                    cooldown = self._connection_cooldown[uri]
                    if cooldown > 512:
                        self._connection_cooldown[uri] = 1
                    if time.time() > attempted + cooldown:
                        ready_candidates.append(uri)
                else:
                    ready_candidates.append(uri)
                    self._connection_cooldown[uri] = 1
        return ready_candidates

    def retry_static_peering(self):
        with self._lock:
            # Endpoints that have reached their retry count and should be
            # removed
            to_remove = []
            for endpoint in self._initial_peer_endpoints:
                connection_id = None
                try:
                    connection_id = \
                        self._network.get_connection_id_by_endpoint(endpoint)
                except KeyError:
                    pass

                static_peer_info = self._static_peer_status[endpoint]
                if connection_id is not None:
                    if connection_id in self._connection_statuses:
                        # Endpoint is already a Peer
                        if self._connection_statuses[connection_id] == \
                                PeerStatus.PEER:
                            # reset static peering info
                            self._static_peer_status[endpoint] = \
                                StaticPeerInfo(
                                    time=0,
                                    retry_threshold=INITIAL_RETRY_FREQUENCY,
                                    count=0)
                            continue

                if (time.time() - static_peer_info.time) > \
                        static_peer_info.retry_threshold:
                    LOGGER.debug("Endpoint has not completed authorization in "
                                 "%s seconds: %s",
                                 static_peer_info.retry_threshold,
                                 endpoint)
                    if connection_id is not None:
                        # If the connection exists remove it before retrying to
                        # authorize.
                        try:
                            self._network.remove_connection(connection_id)
                        except KeyError:
                            pass

                    if static_peer_info.retry_threshold == \
                            MAXIMUM_STATIC_RETRY_FREQUENCY:
                        if static_peer_info.count >= MAXIMUM_STATIC_RETRIES:
                            # Unable to peer with endpoint
                            to_remove.append(endpoint)
                            continue
                        else:
                            # At maximum retry threashold, increment count
                            self._static_peer_status[endpoint] = \
                                StaticPeerInfo(
                                    time=time.time(),
                                    retry_threshold=min(
                                        static_peer_info.retry_threshold * 2,
                                        MAXIMUM_STATIC_RETRY_FREQUENCY),
                                    count=static_peer_info.count + 1)
                    else:
                        self._static_peer_status[endpoint] = \
                            StaticPeerInfo(
                                time=time.time(),
                                retry_threshold=min(
                                    static_peer_info.retry_threshold * 2,
                                    MAXIMUM_STATIC_RETRY_FREQUENCY),
                                count=0)

                    LOGGER.debug("attempting to peer with %s", endpoint)
                    self._network.add_outbound_connection(endpoint)
                    self._temp_endpoints[endpoint] = EndpointInfo(
                        EndpointStatus.PEERING,
                        time.time(),
                        INITIAL_RETRY_FREQUENCY)

            for endpoint in to_remove:
                # Endpoints that have reached their retry count and should be
                # removed
                self._initial_peer_endpoints.remove(endpoint)
                del self._static_peer_status[endpoint]

    def add_candidate_peer_endpoints(self, peer_endpoints):
        """Adds candidate endpoints to the list of endpoints to
        attempt to peer with.

        Args:
            peer_endpoints ([str]): A list of public uri's which the
                validator can attempt to peer with.
        """
        with self._lock:
            for endpoint in peer_endpoints:
                if endpoint not in self._candidate_peer_endpoints:
                    self._candidate_peer_endpoints.append(endpoint)

    def set_connection_status(self, connection_id, status):
        with self._lock:
            self._connection_statuses[connection_id] = status
            if status == PeerStatus.PEER:
                uri = self._network.connection_id_to_endpoint(connection_id)
                self._connection_cooldown[uri] = 1

    def get_connection_status(self, connection_id):
        with self._lock:
            return self._connection_statuses.get(connection_id)

    def get_connection_statuses(self):
        with self._lock:
            return copy.copy(self._connection_statuses)

    def remove_connection_status(self, connection_id):
        with self._lock:
            if connection_id in self._connection_statuses:
                del self._connection_statuses[connection_id]

    def remove_temp_endpoint(self, endpoint):
        with self._lock:
            if endpoint in self._temp_endpoints:
                del self._temp_endpoints[endpoint]

    def _check_temp_endpoints(self):
        with self._lock:
            temp_endpoints = copy.copy(self._temp_endpoints)
        nonresponsive = []
        for endpoint in temp_endpoints:
            endpoint_info = temp_endpoints[endpoint]
            if (time.time() - endpoint_info.time) > \
                    endpoint_info.retry_threshold:
                LOGGER.debug("Endpoint has not completed authorization in "
                                "%s seconds: %s",
                                endpoint_info.retry_threshold,
                                endpoint)
                nonresponsive.append(endpoint)
                try:
                    # If the connection exists remove it before retrying to
                    # authorize. If the connection does not exist, a
                    # KeyError will be thrown.
                    conn_id = \
                        self._network.get_outbound_connection_id_by_endpoint(
                            endpoint)
                    self._network.remove_connection(conn_id)
                except KeyError:
                    pass
                peer = self._network._get_inbound_connection(endpoint)
                if peer:
                    self._gossip.unregister_peer(peer)
                    self._remove_temporary_connection(peer)
        with self._lock:
            for endpoint in nonresponsive:
                if endpoint in self._temp_endpoints:
                    del self._temp_endpoints[endpoint]

    def _refresh_peer_list(self, peers):
        for conn_id in peers:
            try:
                self._network.get_connection_id_by_endpoint(
                    peers[conn_id])
            except KeyError:
                LOGGER.debug("removing peer %s because "
                             "connection went away",
                             peers[conn_id])

                self._gossip.unregister_peer(conn_id)
                self.remove_connection_status(conn_id)

    def _refresh_connection_list(self):
        with self._lock:
            closed_connections = []
            for connection_id in self._connection_statuses:
                if not self._network.has_connection(connection_id):
                    closed_connections.append(connection_id)

            for connection_id in closed_connections:
                del self._connection_statuses[connection_id]

    def _get_peers_of_peers(self, peers):
        get_peers_request = GetPeersRequest()

        for conn_id in peers:
            try:
                self._network.send(
                    validator_pb2.Message.GOSSIP_GET_PEERS_REQUEST,
                    get_peers_request.SerializeToString(),
                    conn_id)
            except ValueError:
                LOGGER.debug("Peer disconnected: %s", conn_id)

    def _get_peers_of_endpoints(self, peers, endpoints):
        get_peers_request = GetPeersRequest()

        for endpoint in endpoints:
            conn_id = None
            try:
                conn_id = self._network.get_connection_id_by_endpoint(
                    endpoint)

            except KeyError:
                # If the connection does not exist, send a connection request
                with self._lock:
                    if endpoint in self._temp_endpoints:
                        continue

                    self._temp_endpoints[endpoint] = EndpointInfo(
                        EndpointStatus.TOPOLOGY,
                        time.time(),
                        INITIAL_RETRY_FREQUENCY)

                    self._network.add_outbound_connection(endpoint)

            # If the connection does exist, request peers.
            if conn_id is not None:
                if conn_id in peers:
                    # connected and peered - we've already sent peer request
                    continue
                else:
                    with self._lock:
                        # connected but not peered
                        if endpoint in self._temp_endpoints:
                            # Endpoint is not yet authorized, do not request peers
                            continue

                    try:
                        self._network.send(
                            validator_pb2.Message.GOSSIP_GET_PEERS_REQUEST,
                            get_peers_request.SerializeToString(),
                            conn_id)
                    except ValueError:
                        LOGGER.debug("Connection disconnected: %s", conn_id)

    def _peer_with_endpoint(self, endpoint):
        connect = False
        with self._lock:
            if not endpoint in self._temp_endpoints:
                connect = True
                self._temp_endpoints[endpoint] = EndpointInfo(
                    EndpointStatus.PEERING,
                    time.time(),
                    INITIAL_RETRY_FREQUENCY)
        if connect:
            self._network.add_outbound_connection(endpoint)

    def _attempt_to_peer_with_endpoint(self, endpoint):
        LOGGER.debug("Attempting to connect/peer with %s", endpoint)

        # check if the connection exists, if it does - send,
        # otherwise create it
        try:
            connection_id = \
                self._network.get_connection_id_by_endpoint(
                    endpoint)

            # register_request = PeerRegisterRequest(
            #     endpoint=self._endpoint,
            #     protocol_version=NETWORK_PROTOCOL_VERSION)

            # self._network.send(
            #     validator_pb2.Message.GOSSIP_REGISTER,
            #     register_request.SerializeToString(),
            #     connection_id,
            #     callback=partial(
            #         self._peer_callback,
            #         endpoint=endpoint,
            #         connection_id=connection_id))
        except KeyError:
            # if the connection uri wasn't found in the network's
            # connections, it raises a KeyError and we need to add
            # a new outbound connection
            self._peer_with_endpoint(endpoint)

    def _reset_candidate_peer_endpoints(self):
        with self._lock:
            self._candidate_peer_endpoints = []
            self._connection_attempted = {}
            self._connection_cooldown = {}

    def _peer_callback(self, request, result, connection_id, endpoint=None):
        ack = NetworkAcknowledgement()
        ack.ParseFromString(result.content)

        if ack.status == ack.ERROR:
            LOGGER.debug("Peering request to %s (%s) was NOT successful",
                            connection_id, endpoint)
            self._remove_temporary_connection(connection_id)
        elif ack.status == ack.OK:
            out_conn_id = self._gossip._get_outbound_connection(endpoint)
            in_conn_id = self._gossip._get_inbound_connection(endpoint)
            if in_conn_id:
                LOGGER.debug("Peering request to %s (%s) was successfull", connection_id, endpoint)
                self._gossip._topology._remove_temporary_connection(out_conn_id)
                try:
                    self._gossip.register_peer(in_conn_id, endpoint)
                    self._gossip.send_block_request("HEAD", in_conn_id)
                except PeeringException as e:
                    # Remove unsuccessful peer
                    LOGGER.warning('Unable to successfully peer with '
                                    'connection_id: %s (%s), due to %s',
                                    in_conn_id, endpoint, str(e))
                    self._gossip._topology._remove_temporary_connection(in_conn_id)
            else:
                LOGGER.debug("Peering request to %s (%s) is pending", connection_id, endpoint)

    def _remove_temporary_connection(self, connection_id):
        status = self.get_connection_status(connection_id)
        if status == PeerStatus.TEMP:
            LOGGER.debug("Closing connection to %s", connection_id)
            msg = DisconnectMessage()
            try:
                self._network.send(validator_pb2.Message.NETWORK_DISCONNECT,
                                   msg.SerializeToString(),
                                   connection_id)
            except ValueError:
                pass
            self.remove_connection_status(connection_id)
            self._network.remove_connection(connection_id)
        elif status == PeerStatus.PEER:
            LOGGER.debug("Connection close request for peer ignored: %s",
                         connection_id)
        elif status is None:
            LOGGER.debug("Connection close request for unknown connection "
                         "ignored: %s",
                         connection_id)

    def connect_success(self, connection_id):
        """
        Check to see if the successful connection is meant to be peered with.
        If not, it should be used to get the peers from the endpoint.
        """
        endpoint = self._network.connection_id_to_endpoint(connection_id)
        with self._lock:
            endpoint_info = self._temp_endpoints.get(endpoint)

            LOGGER.debug("Endpoint has completed authorization: %s (id: %s)",
                        endpoint,
                        connection_id)
            if endpoint_info is None:
                LOGGER.debug("Received unknown endpoint: %s", endpoint)

            elif endpoint_info.status == EndpointStatus.PEERING:
                self._connect_success_peering(connection_id, endpoint)

            elif endpoint_info.status == EndpointStatus.TOPOLOGY:
                self._connect_success_topology(connection_id)

            else:
                LOGGER.debug("Endpoint has unknown status: %s", endpoint)

            if endpoint in self._temp_endpoints:
                del self._temp_endpoints[endpoint]

    def _connect_success_peering(self, connection_id, endpoint):
        LOGGER.debug("Connection to %s succeeded", connection_id)

        register_request = PeerRegisterRequest(
            endpoint=self._endpoint,
            protocol_version=NETWORK_PROTOCOL_VERSION)
        self.set_connection_status(connection_id, PeerStatus.TEMP)
        try:
            self._network.send(
                validator_pb2.Message.GOSSIP_REGISTER,
                register_request.SerializeToString(),
                connection_id,
                callback=partial(
                    self._peer_callback,
                    connection_id=connection_id,
                    endpoint=endpoint))
        except ValueError:
            LOGGER.debug("Connection disconnected: %s", connection_id)

    def _connect_success_topology(self, connection_id):
        LOGGER.debug("Connection to %s succeeded for topology request",
                     connection_id)
        self.set_connection_status(connection_id, PeerStatus.TEMP)
        get_peers_request = GetPeersRequest()

        def callback(request, result):
            # request, result are ignored, but required by the callback
            self._remove_temporary_connection(connection_id)

        try:
            self._network.send(
                validator_pb2.Message.GOSSIP_GET_PEERS_REQUEST,
                get_peers_request.SerializeToString(),
                connection_id,
                callback=callback)
        except ValueError:
            LOGGER.debug("Connection disconnected: %s", connection_id)
