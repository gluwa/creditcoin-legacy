#
#    Copyright(c) 2018 Gluwa, Inc.
#
#    This file is part of Creditcoin.
#
#    Creditcoin is free software: you can redistribute it and/or modify
#    it under the terms of the GNU Lesser General Public License as published by
#    the Free Software Foundation, either version 3 of the License, or
#    (at your option) any later version.
#
#    This program is distributed in the hope that it will be useful,
#    but WITHOUT ANY WARRANTY; without even the implied warranty of
#    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
#    GNU Lesser General Public License for more details.
#
#    You should have received a copy of the GNU Lesser General Public License
#    along with Creditcoin. If not, see <https://www.gnu.org/licenses/>.
#

# pylint: disable=inconsistent-return-statements

import time
import random
import hashlib
import logging
import sys
import atexit
import multiprocessing as mp
from threading import RLock

from sawtooth_validator.journal.block_wrapper import BlockWrapper
from sawtooth_validator.journal.consensus.consensus \
    import BlockPublisherInterface
from sawtooth_validator.journal.consensus.consensus \
    import BlockVerifierInterface
from sawtooth_validator.journal.consensus.consensus \
    import ForkResolverInterface

from sawtooth_validator.state.settings_view import SettingsView
from sawtooth_validator.journal.block_validator import BlockValidationAborted

LOGGER = logging.getLogger(__name__)

POW = b'PoW'
GLUE = b':'
EXPECTED_BLOCK_INTERVAL = 60
DIFFICULTY_ADJUSTMENT_BLOCK_COUNT = 10
DIFFICULTY_TUNING_BLOCK_COUNT = 100
DIFFICULTY_ENFORCING_INTERVAL = EXPECTED_BLOCK_INTERVAL * 30 #must be greater than EXPECTED_BLOCK_INTERVAL
INITIAL_DIFFICULTY = 22
IDX_POW = 0
IDX_DIFFICULTY = 1
IDX_NONCE = 2
IDX_TIME = 3

class _SolverCommand:
    START = 0
    STOP = 1
    SWAP = 2

class _SolverState:
    HASH = 3
    WORKING = 4
    STOPPED = 5 #must have the same payload structure as HASH
    ERROR = 6

SOLVER_PRIM = None
SOLVER_PERF = None
DIFFICULTY_VALIDATOR = None

@atexit.register
def _cleanup():
    if SOLVER_PRIM is not None and SOLVER_PRIM._process.is_alive():
        SOLVER_PRIM._process.terminate()
    if SOLVER_PERF is not None and SOLVER_PERF._process.is_alive():
        SOLVER_PERF._process.terminate()

class _Solver:
    def __init__(self):
        ctx = mp.get_context('spawn')
        self._comm, responder = ctx.Pipe()
        self._process = ctx.Process(target=self.process, args=(responder,))
        self._state = _SolverState.STOPPED
        self._payload = [_SolverState.STOPPED, 'solver:Unitialized', 'actual_solver:Unitialized', 'id:Unitialized', 'difficulty:Unitialized', 'b_nonce:Unitialized']
        self._process.start()
        responder.close()

    @staticmethod
    def process(responder):
        solver = '<Solver hasn\'t been set>'
        actual_solver = solver
        try:
            # standby event loop
            while True:
                command = responder.recv()
                action = command[0]
                actual_solver = command[1]
                solver = actual_solver
                if action == _SolverCommand.STOP:
                    continue
                elif action == _SolverCommand.SWAP:
                    continue
                elif action == _SolverCommand.START:
                    id = command[2]
                    difficulty = command[3]
                    encodedBlockID = command[4]
                    encodedPublicKey = command[5]
                    nonce = random.randrange(sys.maxsize)

                    difficulty_so_far = 0
                    b_nonce_so_far = None

                    responder.send([_SolverState.WORKING,solver,actual_solver])
                    # working event loop
                    while True:
                        b_nonce = str(nonce).encode()
                        digest = _Helper.build_digest_with_encoded_data(encodedBlockID, encodedPublicKey, b_nonce)
                        digest_difficulty = _Helper._count_leading_zeroes(digest)
                        if digest_difficulty >= difficulty:
                            responder.send([_SolverState.HASH, solver, actual_solver, id, difficulty, b_nonce])
                            difficulty = digest_difficulty + 1
                            nonce = random.randrange(sys.maxsize)
                        else:
                            nonce = nonce + 1
                        if digest_difficulty >= difficulty_so_far:
                            difficulty_so_far = digest_difficulty
                            b_nonce_so_far = b_nonce
                        if responder.poll():
                            command = responder.recv()
                            action = command[0]
                            actual_solver = command[1]
                            if action == _SolverCommand.SWAP:
                                responder.send([_SolverState.WORKING,solver,actual_solver])
                                continue
                            elif action == _SolverCommand.STOP:
                                responder.send([_SolverState.STOPPED, solver, actual_solver, id, difficulty_so_far, b_nonce_so_far])
                            else:
                                responder.send([_SolverState.ERROR, solver, actual_solver, 'unhandled event {}'.format(action)])
                            break
                else:
                    responder.send([_SolverState.ERROR, solver, actual_solver, 'Unhandled event {}'.format(action)])

        except KeyboardInterrupt:
            responder.send([_SolverState.STOPPED, solver, actual_solver, 'KeyboardInterrrupt: Shutting down', 'difficulty:Ignored', 'b_nonce:Ignored'])
            return
        except BaseException:
            responder.send([_SolverState.ERROR, solver, actual_solver, 'Critical: unhandled exception in solver, exiting process()'])
    
    @property
    def state(self) -> _SolverState:
        """state is lazy, it will update itself when attempting to read it. There is no setter method for state,
        call an action command and wait for the state to update itself on the next call to this method.

        Returns:
            _SolverState: Last event state received from the worker process.
        """        
        self._drain_sender_event_queue_and_update_state()
        return self._state

    def _drain_sender_event_queue_and_update_state(self):
        payload = None
        state = None
        while self._comm.poll():
            try:
                payload =self._comm.recv()
            except EOFError:
                LOGGER.warning("Solver channel closed. State is no longer being updated")
                return
            #LOGGER.debug("Got from payload {}".format(payload)) #for TRACING
            state = payload[0]
            if state == _SolverState.ERROR:
                error_str = "---Solver error, error({}, {}): {}".format( payload[1], payload[2], payload[3])
                LOGGER.error(error_str)
                raise AssertionError(error_str)
        if state is not None and payload is not None:
            self._state = state
            self._payload = payload
    
    def _stop_solver(self,solver_str:str):
        LOGGER.debug("Stop call {}".format(solver_str))
        self._comm.send([_SolverCommand.STOP, solver_str])

    def _swap_solver(self,solver_str:str):
        LOGGER.debug("Swap call {}".format(solver_str))
        self._comm.send([_SolverCommand.SWAP, solver_str])
    
    def _start_solver(self,solver_str:str,start_time,difficulty,encoded_previous_block_id,encoded_signer_public_key):
        LOGGER.debug("Start solver call {}".format(solver_str))
        if self.state != _SolverState.STOPPED:
            raise RuntimeError("Solver {} is not ready to be started".format(solver_str))
        self._comm.send([_SolverCommand.START, solver_str, start_time, difficulty,encoded_previous_block_id,encoded_signer_public_key])


class _Helper:
    @staticmethod
    def valid_digest(digest, difficulty):
        zeroes = _Helper._count_leading_zeroes(digest)
        return zeroes >= difficulty

    @staticmethod
    def _count_leading_zeroes(digest):
        """
        counts leading zero bits in digest until matches or exceeds difficulty, if difficulty is given,
        otherwise, counts all leading zero bits in digest
        """
        count = 0
        for b in digest:
            if b > 0:
                if b >= 128:
                    pass
                elif b >= 64:
                    count = count + 1
                elif b >= 32:
                    count = count + 2
                elif b >= 16:
                    count = count + 3
                elif b >= 8:
                    count = count + 4
                elif b >= 4:
                    count = count + 5
                elif b >= 2:
                    count = count + 6
                else:
                    count = count + 7
                break
            else:
                count = count + 8
        return count

    @staticmethod
    def build_digest(block_header, b_nonce):
        return _Helper.build_digest_with_encoded_data(block_header.previous_block_id.encode(), block_header.signer_public_key.encode(), b_nonce)

    @staticmethod
    def build_digest_with_encoded_data(encodedBlockID, encodedPublicKey, b_nonce):
        sha = hashlib.sha256()
        sha.update(encodedBlockID)
        sha.update(encodedPublicKey)
        sha.update(b_nonce)
        return sha.digest()

class _DifficultyValidator():
    """
    A class that is responsible for calculating the difficulty of a Block.
    """
    def __init__(self, block_cache, expected_block_interval, difficulty_adjustment_block_count, difficulty_tuning_block_count):
        self._block_cache = block_cache

        self._expected_block_interval = expected_block_interval
        self._difficulty_adjustment_block_count = difficulty_adjustment_block_count
        self._difficulty_tuning_block_count = difficulty_tuning_block_count

    def _get_elapsed_time(self, prev_block, prev_consensus, cur_time, total_count):
        b_last_adjusted_block_time = prev_consensus[IDX_TIME]
        count = 1

        while True:
            prev_block = self._block_cache[prev_block.previous_block_id]
            prev_consensus = prev_block.consensus.split(GLUE)
            if prev_consensus[IDX_POW] != POW:
                break
            count = count + 1
            b_last_adjusted_block_time = prev_consensus[IDX_TIME]
            if count >= total_count:
                break

        last_adjusted_block_time = float(b_last_adjusted_block_time.decode())
        time_taken = cur_time - last_adjusted_block_time
        time_expected = count * self._expected_block_interval
        return time_taken, time_expected

    def get_adjusted_difficulty(self, prev_block, prev_consensus, cur_time):
        difficulty = int(prev_consensus[IDX_DIFFICULTY].decode())
        if prev_block.block_num % self._difficulty_tuning_block_count == 0:
            time_taken, time_expected = self._get_elapsed_time(prev_block, prev_consensus, cur_time, self._difficulty_tuning_block_count)

            if time_taken < time_expected:
                if difficulty < 255:
                    difficulty = difficulty + 1
            elif time_taken > time_expected:
                if difficulty > 0:
                    difficulty = difficulty - 1

        elif prev_block.block_num % self._difficulty_adjustment_block_count == 0:
            time_taken, time_expected = self._get_elapsed_time(prev_block, prev_consensus, cur_time, self._difficulty_adjustment_block_count)

            if time_taken < time_expected / 2:
                if difficulty < 255:
                    difficulty = difficulty + 1
            elif time_taken > time_expected * 2:
                if difficulty > 0:
                    difficulty = difficulty - 1

        return difficulty
    
    def validate_difficulty(self, prev_block, prev_consensus, consensus):
        expected_difficulty = self.get_adjusted_difficulty(prev_block, prev_consensus, float(consensus[IDX_TIME]))
        block_difficulty = consensus[IDX_DIFFICULTY]
        if int(block_difficulty) < int(expected_difficulty):
            now = time.time()
            prev_block_time = float(prev_consensus[IDX_TIME].decode())
            this_block_time = float(consensus[IDX_TIME].decode())
            if this_block_time >= now or this_block_time <= prev_block_time or (this_block_time - prev_block_time) < DIFFICULTY_ENFORCING_INTERVAL:
                LOGGER.debug("Block difficulty differs from expected difficulty: received: %s, expected: %s", block_difficulty, expected_difficulty)
                return False
        return True

    def update_difficulty_settings(self, settings_view):
        self._expected_block_interval = settings_view.get_setting("sawtooth.consensus.pow.seconds_between_blocks", self._expected_block_interval, int)
        self._difficulty_adjustment_block_count = settings_view.get_setting("sawtooth.consensus.pow.difficulty_adjustment_block_count", self._difficulty_adjustment_block_count, int)
        self._difficulty_tuning_block_count = settings_view.get_setting("sawtooth.consensus.pow.difficulty_tuning_block_count", self._difficulty_tuning_block_count, int)


class BlockPublisher(BlockPublisherInterface):
    """PoW consensus uses genesis utility to configure Min/MaxWaitTime
     to determine when to claim a block.
     Default MinWaitTime to zero and MaxWaitTime is 0 or unset,
     ValidBlockPublishers default to None or an empty list.
     PoW Consensus (BlockPublisher) will read these settings
     from the StateView when Constructed.
    """

    def __init__(self,
                 block_cache,
                 state_view_factory,
                 batch_publisher,
                 data_dir,
                 config_dir,
                 validator_id):
        super().__init__(
            block_cache,
            state_view_factory,
            batch_publisher,
            data_dir,
            config_dir,
            validator_id)

        global SOLVER_PRIM
        global SOLVER_PERF
        global DIFFICULTY_VALIDATOR

        self._block_cache = block_cache
        self._state_view_factory = state_view_factory

        self._start_time = 0

        self._valid_block_publishers = None
        self._lock = RLock()

        self._remaining_time = 0

        if SOLVER_PRIM is None:
            SOLVER_PRIM = _Solver()
        if SOLVER_PERF is None:
            SOLVER_PERF = _Solver()
        if DIFFICULTY_VALIDATOR is None:
            DIFFICULTY_VALIDATOR = _DifficultyValidator(self._block_cache, EXPECTED_BLOCK_INTERVAL, DIFFICULTY_ADJUSTMENT_BLOCK_COUNT, DIFFICULTY_TUNING_BLOCK_COUNT)
            state_view = BlockWrapper.state_view_for_block(self._block_cache.block_store.chain_head, self._state_view_factory)
            DIFFICULTY_VALIDATOR.update_difficulty_settings(SettingsView(state_view))

    def initialize_block(self, block_header):
        """Do initialization necessary for the consensus to claim a block,
        this may include initiating voting activates, starting proof of work
        hash generation, or create a PoET wait timer.

        Args:
            block_header (BlockHeader): the BlockHeader to initialize.
        Returns:
            True
        """

        # Using the current chain head, we need to create a state view so we
        # can get our config values.
        state_view = BlockWrapper.state_view_for_block(self._block_cache.block_store.chain_head, self._state_view_factory)

        self._start_time = time.time()
        #LOGGER.debug("init block with time {} in object {}".format(self._start_time,id(self))) #for TRACING

        settings_view = SettingsView(state_view)
        self._valid_block_publishers = settings_view.get_setting("sawtooth.consensus.valid_block_publisher", self._valid_block_publishers, list)

        prev_block = self._block_cache[block_header.previous_block_id]
        prev_consensus = prev_block.consensus.split(GLUE)

        if prev_consensus[IDX_POW] != POW:
            difficulty = INITIAL_DIFFICULTY
        else:
            global DIFFICULTY_VALIDATOR
            difficulty = DIFFICULTY_VALIDATOR.get_adjusted_difficulty(prev_block, prev_consensus, self._start_time)
        # difficulty = INITIAL_DIFFICULTY #this is for debugging (LOW DIFFICULTY) 
        global SOLVER_PRIM

        with self._lock:
            try:
                #LOGGER.info('---initialize_block#297 Starting SOLVER_PRIM')
                SOLVER_PRIM._start_solver('SOLVER_PRIM',self._start_time, difficulty,block_header.previous_block_id.encode(), block_header.signer_public_key.encode())
            except RuntimeError as e:
                LOGGER.debug(str(e))
                return False
            except BaseException as e:
                LOGGER.critical('---initialize_block Unexpected exception %s', str(e))
                return False
        LOGGER.info('New block using Gluwa PoW 1.1 consensus')
        return True

    def check_publish_block(self, block_header):
        """Check if a candidate block is ready to be claimed.

        block_header (BlockHeader): the block_header to be checked if it
            should be claimed
        Returns:
            Boolean: True if the candidate block_header should be claimed.
        """

        global SOLVER_PRIM
        global SOLVER_PERF

        if self._valid_block_publishers and block_header.signer_public_key not in self._valid_block_publishers:
            return False

        with self._lock:
            try:
                result = self._read_solver(SOLVER_PRIM, 'SOLVER_PRIM', block_header)
                if result == _SolverState.WORKING:
                    if time.time() - self._start_time < DIFFICULTY_ENFORCING_INTERVAL:
                        return False
                    SOLVER_PRIM._stop_solver("SOLVER_PRIM") #it won't be running as a performance solver either, as DIFFICULTY_ENFORCING_INTERVAL is greater than EXPECTED_BLOCK_INTERVAL
                    while True:
                        result = self._read_solver(SOLVER_PRIM, 'SOLVER_PRIM', block_header)
                        if result == _SolverState.STOPPED:
                            self._update_consensus(SOLVER_PRIM, 'SOLVER_PRIM', block_header)
                            break
                elif result == _SolverState.STOPPED:
                    LOGGER.debug("CPB consensus stopped")
                    return False
                # else _SolverState.HASH found
                SOLVER_PERF._stop_solver('SOLVER_PERF')

            except BaseException as e:
                LOGGER.critical('---check_publish_block Unexpected unhandled exception %s', str(e))
                LOGGER.critical("Solver undefined behaviour")
                return False

            self.get_remaining_time()
        return True

    def on_accepted(self):

        global SOLVER_PRIM
        global SOLVER_PERF

        # LOGGER.info('--- swapping solvers')
        with self._lock:
            tmp = SOLVER_PERF
            SOLVER_PERF = SOLVER_PRIM
            SOLVER_PRIM = tmp

            SOLVER_PRIM._swap_solver("SOLVER_PRIM")
            SOLVER_PERF._swap_solver("SOLVER_PERF")

    def get_remaining_time(self):
        #settings not updated because this function is always called after initialize_block which updates settings for the current candidate_block
        global DIFFICULTY_VALIDATOR
        remaining_time = DIFFICULTY_VALIDATOR._expected_block_interval - (time.time() - self._start_time)
        if remaining_time < 0:
            remaining_time = 0
        self._remaining_time = remaining_time
        return remaining_time

    def _update_consensus(self, solver, solver_str, block_header):
        difficulty = None
        b_nonce = None
        if self._start_time != solver._payload[3]:
            LOGGER.error('---_read_solver {} error: Stale hash  {} -- Current start_time {}'.format(solver_str, solver._payload, self._start_time))
            raise AssertionError
        difficulty = solver._payload[4]
        b_nonce = solver._payload[5]
        #LOGGER.info('--- polling %s - found a hash', solver_str)
        consensus = GLUE.join([POW, str(difficulty).encode(), b_nonce, str(self._start_time).encode()])
        block_header.consensus = consensus
        LOGGER.info("Built PoW consensus {}".format(consensus))
        #LOGGER.info('--- polling %s - has data', solver_str)

    def _read_solver(self, solver, solver_str, block_header):
        """Build consensus if a solution is available.
        (reminder) solver's state updates lazily on read.

        Args:
            solver ([type]): global solver object.
            solver_str ([type]): solver's string id.
            block_header ([type]): block header to be updated if a valid hash is found.

        Raises:
            AssertionError: The found hash doesn't pertain to the current Block.
            AssertionError: Unexpected Error.

        Returns:
            _SolverState: Propagate the state.
        """
        # LOGGER.info('--- polling %s', solver_str)
        try:
            state = solver.state
        except RuntimeError as e:
            LOGGER.error('---_read_solver {} payload: {}'.format(solver_str, solver._payload))

        if state == _SolverState.HASH:
            self._update_consensus(solver, solver_str, block_header)
            return _SolverState.HASH
        elif state == _SolverState.WORKING or state == _SolverState.STOPPED:
            return state
        else:
            LOGGER.error('---_read_solver {} error: unexpected solver state event response {}'.format(solver_str, solver._payload))
            raise AssertionError

    def finalize_block(self, block_header):
        """Finalize a block to be claimed. Provide any signatures and
        data updates that need to be applied to the block before it is
        signed and broadcast to the network.

        Args:
            block_header (BlockHeader): The candidate block that needs to be
            finalized.
        Returns:
            True
        """
        return True

    def update_block(self, block_header) -> bool:
        """Update block by ThreadAux if there is a better hash.

        Args:
            block_header ([type]): [description]

        Returns:
            bool: returns whether a consensus (better hash) has been built or not.
        """

        global SOLVER_PERF
        LOGGER.debug("Updating by thread aux")
        LOGGER.debug("init_time {} from object {} in thread aux".format(self._start_time, id(self)))
        with self._lock:
            try:
                solver_state = self._read_solver(SOLVER_PERF, 'SOLVER_PERF', block_header)
                if solver_state == _SolverState.HASH:
                    LOGGER.debug("hash found by thread aux")
                    # ask for a swap, the solver will be set to working on the same puzzle
                    # wihtout this, on reentry, there is no distinction between an already used hash or a new one.
                    # we could be create a new command for clarity with the same effect as a swap in the working loop.
                    SOLVER_PERF._swap_solver("SOLVER_PERF")
                    return True

            except BaseException as e:
                LOGGER.critical('---update_block() Unexpected exception %s', str(e))
        return False

    def on_cancel_publish_block(self):
        LOGGER.debug("on cancel publish block POW")
        global SOLVER_PRIM
        global SOLVER_PERF
        with self._lock:
            try:
                # LOGGER.info('---on_cancel_publish_block#440 cancelling SOLVER_PRIM')
                SOLVER_PRIM._stop_solver('SOLVER_PRIM')
                SOLVER_PERF._stop_solver('SOLVER_PERF')
            except BaseException as e:
                LOGGER.critical('---on_cancel_publish_block Unexpected exception %s', str(e))

class BlockVerifier(BlockVerifierInterface):
    """PoW BlockVerifier implementation
    """

    # pylint: disable=useless-super-delegation

    def __init__(self,
                 block_cache,
                 state_view_factory,
                 data_dir,
                 config_dir,
                 validator_id):
        super().__init__(
            block_cache,
            state_view_factory,
            data_dir,
            config_dir,
            validator_id)

        self._block_cache = block_cache
        self._state_view_factory = state_view_factory

    def verify_block(self, block_wrapper):
        consensus = block_wrapper.header.consensus.split(GLUE)
        if consensus[IDX_POW] != POW:
            return False

        # Verify that the Block follows the expected sequence in difficulty.
        try:
            prev_block = self._block_cache[block_wrapper.header.previous_block_id]
        except KeyError:
            LOGGER.debug(
                "Block failed validation due to missing predecessor at consensus' verify_block: %s", block_wrapper)
            return False

        prev_consensus = prev_block.consensus.split(GLUE)
        if prev_consensus[IDX_POW] == POW:
            global DIFFICULTY_VALIDATOR
            if not DIFFICULTY_VALIDATOR.validate_difficulty(prev_block, prev_consensus, consensus):
                return False

        b_nonce = consensus[IDX_NONCE]
        difficulty = int(consensus[IDX_DIFFICULTY].decode())
        digest = _Helper.build_digest(block_wrapper.header, b_nonce)

        return _Helper.valid_digest(digest, difficulty)


class ForkResolver(ForkResolverInterface):
    """Provides the fork resolution interface for the BlockValidator to use
    when deciding between 2 forks.
    """

    # pylint: disable=useless-super-delegation

    def __init__(self,
                 block_cache,
                 state_view_factory,
                 data_dir,
                 config_dir,
                 validator_id):
        #super().__init__(
            #block_cache,
            #state_view_factory,
            #data_dir,
            #config_dir,
            #validator_id)
        self._block_cache = block_cache

    #no genesis block check
    def _get_block_at_cur_tip(self, fork_size_diff, block, consensus):
        while fork_size_diff > 0:
            try:
                prev = self._block_cache[block.previous_block_id]
            except KeyError:
                LOGGER.debug("missing predecessor {}".format(block.previous_block_id))
                raise BlockValidationAborted("Tip lookup failed in PoW consensus.")
            consensus = prev.header.consensus.split(GLUE)
            if consensus[IDX_POW] != POW:
                raise TypeError('fork contains non PoW blocks')
            block = prev
            fork_size_diff = fork_size_diff - 1
        return block

    def _verify_difficulties(self, block, id):
        while block.header_signature != id:
            try:
                prev_block = self._block_cache[block.previous_block_id]
            except KeyError:
                LOGGER.debug("missing predecessor {}".format(block.previous_block_id))
                raise BlockValidationAborted("difficulty verification failed in PoW consensus.")

            consensus = block.header.consensus.split(GLUE)
            prev_consensus = prev_block.header.consensus.split(GLUE)
            global DIFFICULTY_VALIDATOR
            if not DIFFICULTY_VALIDATOR.validate_difficulty(prev_block, prev_consensus, consensus):
                return False
            block = prev_block
        return True

    def _process_fork(self, block, id):
        difficulty = 0
        while block.header_signature != id:
            consensus = block.header.consensus.split(GLUE)
            difficulty = difficulty + \
                2 ** int(consensus[IDX_DIFFICULTY].decode())
            try:
                block = self._block_cache[block.previous_block_id]
            except KeyError:
                LOGGER.debug("missing predecessor {}".format(block.previous_block_id))
                raise BlockValidationAborted("process fork failed in PoW consensus.")
        return difficulty

    def compare_forks(self, cur_fork_head, new_fork_head):
        """The longest chain is selected. If they are equal, then the hash
        value of the previous block id and publisher signature is computed.
        The lowest result value is the winning block.
        Args:
            cur_fork_head: The current head of the block chain.
            new_fork_head: The head of the fork that is being evaluated.
        Returns:
            bool: True if choosing the new chain head, False if choosing
            the current chain head.
        """

        # If the new fork head is not PoW consensus, bail out. This should never happen.
        new_consensus = new_fork_head.consensus.split(GLUE)
        if new_consensus[IDX_POW] != POW:
            raise TypeError('New fork head {} is not a PoW block: consensus {}'.format(new_fork_head.identifier[:8],new_fork_head.consensus))

        now = time.time()
        new_time = float(new_consensus[IDX_TIME].decode())
        if (new_time > now + 30):
            LOGGER.warning("New fork head {} is ahead of time {}".format(new_fork_head.identifier[:8], new_time))
            return False

        # If the current fork head is not PoW consensus, check the new fork head to see if its immediate predecessor is the current fork head.
        # If so that means that consensus mode is changing. If not, we are again in a situation that should never happen
        cur_consensus = cur_fork_head.consensus.split(GLUE)
        if cur_consensus[IDX_POW] != POW:
            if new_fork_head.previous_block_id == cur_fork_head.identifier:
                LOGGER.info('Choose new fork %s: New fork head switches consensus to PoW', new_fork_head.identifier[:8])
                return True
            raise TypeError('Trying to compare a PoW block {} to a non-PoW block {} that is not the direct predecessor'.format(new_fork_head.identifier[:8], cur_fork_head.identifier[:8]))

        cur_time = float(cur_consensus[IDX_TIME].decode())

        try:
            new_block = self._get_block_at_cur_tip(new_fork_head.block_num - cur_fork_head.block_num, new_fork_head, new_consensus)
            cur_block = self._get_block_at_cur_tip(cur_fork_head.block_num - new_fork_head.block_num, cur_fork_head, cur_consensus)

            common_ancestop_id = None
            common_ancestop_height = None
            common_ancestop_difficulty = None
            common_ancestop_time = None
            while new_block.header_signature != cur_block.header_signature:
                # at this point we know the outstanding blocks are PoW, check ascendants
                try:
                    new_block = self._block_cache[new_block.previous_block_id]
                except KeyError:
                    LOGGER.debug("Missing predecessor in new chain head {}".format(new_block.previous_block_id))
                    raise BlockValidationAborted("Building common chain failed.")
                # Retrieving our own blocks should not fail.
                cur_block = self._block_cache[cur_block.previous_block_id]

                new_consensus = new_block.header.consensus.split(GLUE)
                cur_consensus = cur_block.header.consensus.split(GLUE)
                #forward check
                if new_consensus[IDX_POW] != POW or cur_consensus[IDX_POW] != POW:
                    # blocks ascendants are no longer PoW: At Genesis? Settings change?
                    raise BlockValidationAborted("Unhandled behaviour.")

            common_ancestop_id = new_block.header_signature
            common_ancestop_height = new_block.block_num
            common_ancestop_difficulty = int(new_consensus[IDX_DIFFICULTY].decode())
            common_ancestop_time = float(new_consensus[IDX_TIME].decode())

            new_fork_len = new_fork_head.block_num - common_ancestop_height

            if not self._verify_difficulties(new_fork_head, common_ancestop_id):
                LOGGER.warning("Blocks in new fork failed to meet the minimal difficulty")
                return False

            cur_fork_len = cur_fork_head.block_num - common_ancestop_height

            new_difficulty = self._process_fork(new_fork_head, common_ancestop_id)
            cur_difficulty = self._process_fork(cur_fork_head, common_ancestop_id)
            # new_difficulty = cur_difficulty + 1 #this is for debugging (LOW DIFFICULTY)
        except BlockValidationAborted as e:
            LOGGER.warning("New fork rejected: {}".format(e))
            return False

        if cur_fork_len > 0:
            cur_av_time_dev = (cur_time - common_ancestop_time) / cur_fork_len
        else:
            cur_av_time_dev = 0
        if new_fork_len > 0:
            new_av_time_dev = (new_time - common_ancestop_time) / new_fork_len
        else:
            new_fork_len = 0

        if new_difficulty > cur_difficulty:
            return True
        elif new_difficulty < cur_difficulty:
            return False
        elif new_difficulty == cur_difficulty:
            if new_av_time_dev < cur_av_time_dev:
                return True

        return False
