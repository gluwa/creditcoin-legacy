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

SOLVER = None

@atexit.register
def _cleanup():
    if SOLVER is not None and SOLVER._process.is_alive():
        SOLVER._process.terminate()

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
                nonce = random.randrange(sys.maxsize)

                while True:
                    id = command[0]
                    difficulty = command[1]
                    encodedBlockID = command[2]
                    encodedPublicKey = command[3]
                    b_nonce = str(nonce).encode()
                    digest = _Helper.build_digest_with_encoded_data(encodedBlockID, encodedPublicKey, b_nonce)
                    if _Helper.valid_digest(digest, difficulty):
                        break
                    if responder.poll():
                        command = responder.recv()
                        nonce = random.randrange(sys.maxsize)
                    else:
                        nonce = nonce + 1
                responder.send([id, difficulty, b_nonce])
        finally:
            return

class _Helper:
    @staticmethod
    def valid_digest(digest, difficulty):
        count = 0;
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
                if count >= difficulty:
                    return True
        return count >= difficulty

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

        global SOLVER

        self._block_cache = block_cache
        self._state_view_factory = state_view_factory

        self._start_time = 0
        self._expected_block_interval = EXPECTED_BLOCK_INTERVAL
        self._difficulty_adjustment_block_count = DIFFICULTY_ADJUSTMENT_BLOCK_COUNT
        self._difficulty_tuning_block_count = DIFFICULTY_TUNING_BLOCK_COUNT

        self._valid_block_publishers = None

        if SOLVER is None:
            SOLVER = _Solver()

    def _get_elapsed_time(self, prev_block, prev_consensus, cur_time, total_count):
        b_last_adjusted_block_time = prev_consensus[IDX_TIME]
        count = 2

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

        SOLVER._comm.send([self._start_time, difficulty, block_header.previous_block_id.encode(), block_header.signer_public_key.encode()])
        LOGGER.info('New block using Gluwa PoW consensus')

        return True

    def check_publish_block(self, block_header):
        """Check if a candidate block is ready to be claimed.

        block_header (BlockHeader): the block_header to be checked if it
            should be claimed
        Returns:
            Boolean: True if the candidate block_header should be claimed.
        """

        if self._valid_block_publishers and block_header.signer_public_key not in self._valid_block_publishers:
            return False

        if not SOLVER._comm.poll():
            return False

        pipe_result = SOLVER._comm.recv()
        while True:
            if self._start_time == pipe_result[0]:
                break
            if not SOLVER._comm.poll():
                return False
            pipe_result = SOLVER._comm.recv()

        difficulty = pipe_result[1]
        b_nonce = pipe_result[2]
        block_header.consensus = GLUE.join([POW, str(difficulty).encode(), b_nonce, str(self._start_time).encode()])

        return True

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

    def _get_difficulty(self, fork_size_diff, block, consensus):
        difficulty = 0
        while fork_size_diff > 0:
            difficulty = difficulty + 2 ** int(consensus[IDX_DIFFICULTY].decode())
            block = self._block_cache[block.previous_block_id]
            consensus = block.header.consensus.split(GLUE)
            if consensus[IDX_POW] != POW:
                break
            fork_size_diff = fork_size_diff - 1
        return block, difficulty

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

        # If the current fork head is not PoW consensus, check the new fork head to see if its immediate predecessor is the current fork head.
        # If so that means that consensus mode is changing.  If not, we are again in a situation that should never happen
        cur_consensus = cur_fork_head.consensus.split(GLUE)
        if cur_consensus[IDX_POW] != POW:
            if new_fork_head.previous_block_id == cur_fork_head.identifier:
                LOGGER.info('Choose new fork %s: New fork head switches consensus to PoW', new_fork_head.identifier[:8])
                return True
            raise TypeError('Trying to compare a PoW block {} to a non-PoW block {} that is not the direct predecessor'.format(new_fork_head.identifier[:8], cur_fork_head.identifier[:8]))

        new_block, new_difficulty = self._get_difficulty(new_fork_head.block_num - cur_fork_head.block_num, new_fork_head, new_consensus)
        cur_block, cur_difficulty = self._get_difficulty(cur_fork_head.block_num - new_fork_head.block_num, cur_fork_head, cur_consensus)

        while True:
            if new_block.header_signature == cur_block.header_signature:
                break
            new_consensus = new_block.header.consensus.split(GLUE)
            cur_consensus = cur_block.header.consensus.split(GLUE)
            if new_consensus[IDX_POW] == POW:
                new_difficulty = new_difficulty + 2 ** int(new_consensus[IDX_DIFFICULTY].decode())
                new_block = self._block_cache[new_block.previous_block_id]
            if cur_consensus[IDX_POW] == POW:
                cur_difficulty = cur_difficulty + 2 ** int(cur_consensus[IDX_DIFFICULTY].decode())
                cur_block = self._block_cache[cur_block.previous_block_id]

        result = new_difficulty > cur_difficulty
        return result
