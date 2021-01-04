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

LOGGER = logging.getLogger(__name__)

POW = b'PoW'
GLUE = b':'
EXPECTED_BLOCK_INTERVAL = 60
DIFFICULTY_ADJUSTMENT_BLOCK_COUNT = 10
DIFFICULTY_TUNING_BLOCK_COUNT = 100
INITIAL_DIFFICULTY = 22
IDX_POW = 0
IDX_DIFFICULTY = 1
IDX_NONCE = 2
IDX_TIME = 3

SOLVER_START = 0
SOLVER_STOP = 1

SOLVER_HASH = 0
SOLVER_WORKING = 1
SOLVER_STOPPED = 2
SOLVER_ERROR = 3

SOLVER_PRIM = None
SOLVER_PERF = None

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
        self._process.start()
        responder.close()

    @staticmethod
    def process(responder):
        try:
            while True:
                command = responder.recv()
                action = command[0]
                if action == SOLVER_STOP:
                    responder.send([SOLVER_STOPPED, 'Stopped'])
                    continue

                id = command[1]
                difficulty = command[2]
                encodedBlockID = command[3]
                encodedPublicKey = command[4]
                nonce = random.randrange(sys.maxsize)

                while True:
                    if responder.poll():
                        command = responder.recv()
                        action = command[0]
                        if action != SOLVER_STOP:
                            responder.send([SOLVER_ERROR, 'Expecting SOLVER_STOP'])
                        responder.send([SOLVER_STOPPED, 'Stopped'])
                        break
                    b_nonce = str(nonce).encode()
                    digest = _Helper.build_digest_with_encoded_data(encodedBlockID, encodedPublicKey, b_nonce)
                    digest_difficulty = _Helper._count_leading_zeroes(digest)
                    if digest_difficulty >= difficulty:
                        responder.send([SOLVER_HASH, id, difficulty, b_nonce])
                        difficulty = digest_difficulty + 1
                        nonce = random.randrange(sys.maxsize)
                    else:
                        nonce = nonce + 1
        finally:
            return

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

        self._block_cache = block_cache
        self._state_view_factory = state_view_factory

        self._start_time = 0
        self._expected_block_interval = EXPECTED_BLOCK_INTERVAL
        self._difficulty_adjustment_block_count = DIFFICULTY_ADJUSTMENT_BLOCK_COUNT
        self._difficulty_tuning_block_count = DIFFICULTY_TUNING_BLOCK_COUNT

        self._valid_block_publishers = None
        self._lock = RLock()

        self._remaining_time = 0

        if SOLVER_PRIM is None:
            SOLVER_PRIM = _Solver()
        if SOLVER_PERF is None:
            SOLVER_PERF = _Solver()

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

    def _get_adjusted_difficulty(self, prev_block, prev_consensus, cur_time):
        difficulty = int(prev_consensus[IDX_DIFFICULTY].decode())
        if prev_block.block_num % self._difficulty_tuning_block_count == 0:
            time_taken, time_expected = self._get_elapsed_time(prev_block, prev_consensus, cur_time, self._difficulty_tuning_block_count)

            if time_taken < time_expected:
                if difficulty < 255:
                    difficulty = difficulty + 1
            elif time_taken > time_expected:
                if  difficulty > 0:
                    difficulty = difficulty - 1
        elif prev_block.block_num % self._difficulty_adjustment_block_count == 0:
            time_taken, time_expected = self._get_elapsed_time(prev_block, prev_consensus, cur_time, self._difficulty_adjustment_block_count)

            if time_taken < time_expected / 2:
                if difficulty < 255:
                    difficulty = difficulty + 1
            elif time_taken > time_expected * 2:
                if  difficulty > 0:
                    difficulty = difficulty - 1

        return difficulty

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

        settings_view = SettingsView(state_view)
        self._expected_block_interval = settings_view.get_setting("sawtooth.consensus.pow.seconds_between_blocks", self._expected_block_interval, int)
        self._difficulty_adjustment_block_count = settings_view.get_setting("sawtooth.consensus.pow.difficulty_adjustment_block_count", self._difficulty_adjustment_block_count, int)
        self._difficulty_tuning_block_count = settings_view.get_setting("sawtooth.consensus.pow.difficulty_tuning_block_count", self._difficulty_tuning_block_count, int)
        self._valid_block_publishers = settings_view.get_setting("sawtooth.consensus.valid_block_publishers", self._valid_block_publishers, list)

        prev_block = self._block_cache[block_header.previous_block_id]
        prev_consensus = prev_block.consensus.split(GLUE)

        if prev_consensus[IDX_POW] != POW:
            difficulty = INITIAL_DIFFICULTY
        else:
            difficulty = self._get_adjusted_difficulty(prev_block, prev_consensus, self._start_time)

        global SOLVER_PRIM

        SOLVER_PRIM._comm.send([SOLVER_START, self._start_time, difficulty, block_header.previous_block_id.encode(), block_header.signer_public_key.encode()])
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

        if not SOLVER_PRIM._comm.poll():
            return False

        result = self._read_solver(SOLVER_PRIM, block_header)
        if result == SOLVER_WORKING:
            return False
        elif result == SOLVER_STOPPED:
            LOGGER.info('Primary solver stopped')
            return False
        elif result == SOLVER_ERROR:
            LOGGER.error('Primary solver errored')
            raise AssertionError
        with self._lock:
            SOLVER_PERF._comm.send([SOLVER_STOP])
            while True:
                pipe_result = SOLVER_PERF._comm.recv()
                if pipe_result[0] == SOLVER_STOPPED:
                    break
            tmp = SOLVER_PERF
            SOLVER_PERF = SOLVER_PRIM
            SOLVER_PRIM = tmp
            remaining_time = 60 - (time.time() - self._start_time)
            if remaining_time < 0:
                remaining_time = 0

        self._remaining_time = remaining_time
        return True

    def get_remaining_time(self):
        return self._remaining_time

    def _read_solver(self, solver, block_header):
        difficulty = None
        b_nonce = None
        pipe_result = solver._comm.recv()
        while True:
            result_type = pipe_result[0]
            if result_type == SOLVER_ERROR:
                LOGGER.error('Solver error: %s', pipe_result[1])
                return SOLVER_ERROR
            elif result_type == SOLVER_STOPPED:
                return SOLVER_STOPPED
            elif result_type != SOLVER_HASH:
                LOGGER.error('Solver error: unexpected responce %s', str(result_type))
                return SOLVER_ERROR
            elif self._start_time == pipe_result[1]:
                difficulty = pipe_result[2]
                b_nonce = pipe_result[3]
            else:
                LOGGER.warning('Solver warning: unexpected id %s', str(pipe_result[1]))
            if not solver._comm.poll():
                if difficulty:
                    break
                return SOLVER_WORKING
            pipe_result = solver._comm.recv()

        block_header.consensus = GLUE.join([POW, str(difficulty).encode(), b_nonce, str(self._start_time).encode()])
        return SOLVER_HASH

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

    def update_block(self, block_header):
        global SOLVER_PERF
        SOLVER_PERF._comm.send([SOLVER_STOP])
        if SOLVER_PERF._comm.poll():
            new_hash = self._read_solver(SOLVER_PERF, block_header)
        return False

    def on_cancel_publish_block(self, block_header):
        global SOLVER_PRIM
        global SOLVER_PERF
        SOLVER_PRIM._comm.send([SOLVER_STOP])
        while True:
            pipe_result = SOLVER_PRIM._comm.recv()
            if pipe_result[0] == SOLVER_STOPPED:
                break
        SOLVER_PERF._comm.send([SOLVER_STOP])
        while True:
            pipe_result = SOLVER_PERF._comm.recv()
            if pipe_result[0] == SOLVER_STOPPED:
                break

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

    def verify_block(self, block_wrapper):
        consensus = block_wrapper.header.consensus.split(GLUE)
        if consensus[IDX_POW] != POW:
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
        super().__init__(
            block_cache,
            state_view_factory,
            data_dir,
            config_dir,
            validator_id)
        self._block_cache = block_cache

    def _get_block_at_cur_tip(self, fork_size_diff, block, consensus):
        while fork_size_diff > 0:
            block = self._block_cache[block.previous_block_id]
            consensus = block.header.consensus.split(GLUE)
            if consensus[IDX_POW] != POW:
                break
            fork_size_diff = fork_size_diff - 1
        return block

    def _get_difficulties(self, block, id):
        difficulties = []
        while block.header_signature != id:
            consensus = block.header.consensus.split(GLUE)
            difficulties.append(int(consensus[IDX_DIFFICULTY].decode()))
            block = self._block_cache[block.previous_block_id]
        return difficulties

    def _process_fork(self, block, id):
        difficulty = 0
        while block.header_signature != id:
            consensus = block.header.consensus.split(GLUE)
            difficulty = difficulty + 2 ** int(consensus[IDX_DIFFICULTY].decode())
            block = self._block_cache[block.previous_block_id]
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
            raise TypeError('New fork head {} is not a PoW block'.format(new_fork_head.identifier[:8]))

        now = time.time()
        new_time = float(new_consensus[IDX_TIME].decode())
        if (new_time > now + 30):
            return False

        # If the current fork head is not PoW consensus, check the new fork head to see if its immediate predecessor is the current fork head.
        # If so that means that consensus mode is changing.  If not, we are again in a situation that should never happen
        cur_consensus = cur_fork_head.consensus.split(GLUE)
        if cur_consensus[IDX_POW] != POW:
            if new_fork_head.previous_block_id == cur_fork_head.identifier:
                LOGGER.info('Choose new fork %s: New fork head switches consensus to PoW', new_fork_head.identifier[:8])
                return True
            raise TypeError('Trying to compare a PoW block {} to a non-PoW block {} that is not the direct predecessor'.format(new_fork_head.identifier[:8], cur_fork_head.identifier[:8]))

        cur_time = float(cur_consensus[IDX_TIME].decode())

        new_block = self._get_block_at_cur_tip(new_fork_head.block_num - cur_fork_head.block_num, new_fork_head, new_consensus)
        cur_block = self._get_block_at_cur_tip(cur_fork_head.block_num - new_fork_head.block_num, cur_fork_head, cur_consensus)

        if (new_block.block_num != cur_block.block_num):
            raise TypeError('New fork contains non PoW blocks')

        common_ancestop_id = None
        common_ancestop_height = None
        common_ancestop_difficulty = None
        common_ancestop_time = None
        while True:
            if new_block.header_signature == cur_block.header_signature:
                common_ancestop_id = new_block.header_signature
                common_ancestop_height = new_block.block_num
                new_consensus = new_block.header.consensus.split(GLUE)
                common_ancestop_difficulty = int(new_consensus[IDX_DIFFICULTY].decode())
                common_ancestop_time = float(new_consensus[IDX_TIME].decode())
                break
            new_consensus = new_block.header.consensus.split(GLUE)
            cur_consensus = cur_block.header.consensus.split(GLUE)
            if new_consensus[IDX_POW] == POW:
                new_block = self._block_cache[new_block.previous_block_id]
            if cur_consensus[IDX_POW] == POW:
                cur_block = self._block_cache[cur_block.previous_block_id]

        new_difficulties = self._get_difficulties(new_fork_head, common_ancestop_id)
        new_fork_len = new_fork_head.block_num - common_ancestop_height
        minimal_difficulty = common_ancestop_difficulty - new_fork_len % DIFFICULTY_ADJUSTMENT_BLOCK_COUNT - new_fork_len % DIFFICULTY_TUNING_BLOCK_COUNT - 3
        if minimal_difficulty < 0:
            minimal_difficulty = 0
        for d in new_difficulties:
            if d < minimal_difficulty:
                return False

        cur_fork_len = cur_fork_head.block_num - common_ancestop_height

        new_difficulty = self._process_fork(new_fork_head, common_ancestop_id)
        cur_difficulty = self._process_fork(cur_fork_head, common_ancestop_id)

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
