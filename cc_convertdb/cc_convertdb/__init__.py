#
#    Copyright(c) 2021 Gluwa, Inc.
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

import cbor
import logging
import os
from collections import deque
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
from io import BytesIO
from sawtooth_validator.database.indexed_database import IndexedDatabase
from sawtooth_validator.database.lmdb_nolock_database import LMDBNoLockDatabase
from sawtooth_validator.journal.block_store import BlockStore

'''
Converts a Hyperledger Sawtooth Merkle Tree database to use shortcuts.
'''

LOGGER = logging.getLogger('cc_convertdb')

class LMDBConverter():

    def __init__(self, data_path, revert=False, blocknums=None, num_workers=4):
        merkle_path = os.path.join(data_path, 'merkle-00.lmdb')
        block_path = os.path.join(data_path, 'block-00.lmdb')

        # Ensure both required files exist.
        if not os.path.exists(merkle_path) or not os.path.exists(block_path):
            err_msg = data_path + ' does not have the following required files:'
            if not os.path.exists(merkle_path):
                err_msg += '\n\t' + merkle_path
            if not os.path.exists(block_path):
                err_msg += '\n\t' + block_path
            raise FileNotFoundError(err_msg)

        # Get the state root hashes of each block requested.
        block_db = IndexedDatabase(
            block_path,
            BlockStore.serialize_block,
            BlockStore.deserialize_block,
            flag='x',
            indexes=BlockStore.create_index_configuration())
        with closing(block_db):
            block_store = BlockStore(block_db)
            if not blocknums:
                blocks = [block_store.chain_head]
            else:
                blocks = [block_store.get_block_by_number(blocknum)
                    for blocknum in blocknums]
            self._state_root_hashes = [block.state_root_hash
                for block in blocks]
        
        self._merkle_db = LMDBNoLockDatabase(merkle_path, 'x')
        self._revert = revert
        self._thread_pool = ThreadPoolExecutor(max(1, num_workers))
    
    def _convert_subtree(self, hash):
        node_bytes = self._merkle_db[hash]
        # Read and parse the first CBOR segment.
        node_bytestream = BytesIO(node_bytes)
        node = cbor.load(node_bytestream)
        # Only convert nodes with single children.
        if len(node['c']) == 1:
            child_path, child_hash = next(iter(node['c'].items()))
            converted_entries, last_shortcut, next_hashes = self._convert_subtree(child_hash)
            # Extend existing shortcut, or create a new one pointing to its child.
            if last_shortcut is not None:
                sub_path, bottom_hash = last_shortcut
            else:
                sub_path, bottom_hash = '', child_hash
            sub_path = child_path + sub_path
            last_shortcut = (sub_path, bottom_hash)
            # Remove the second segment, if it exists.
            converted_entry = node_bytes[:node_bytestream.tell()]
            # Append shortcut if converting.
            if not self._revert:
                converted_entry += cbor.dumps(last_shortcut)
            converted_entries.append((hash, converted_entry))
            return converted_entries, last_shortcut, next_hashes
        else:
            # Otherwise, return the next node hashes to process.
            return [], None, list(node['c'].values())
    
    def convert_tree(self):
        LOGGER.info('Starting conversion...')
        try:
            # Run a subtask for each state root.
            next_tasks = deque(self._thread_pool.submit(self._convert_subtree, hash)
                for hash in self._state_root_hashes)
            tasks_ran = 0
            tasks_pending = len(next_tasks)
            entries_written = 0
            while next_tasks:
                task = next_tasks.popleft()
                try:
                    converted_entries, _, next_hashes = task.result()
                    tasks_ran += 1
                    tasks_pending += len(next_hashes)
                except Exception as ex:
                    if isinstance(ex, KeyboardInterrupt):
                        raise ex
                    # Log the exception, then continue.
                    LOGGER.warning(ex)
                    tasks_pending -= 1
                    continue
                if converted_entries:
                    self._merkle_db.put_multi(converted_entries)
                    entries_written += len(converted_entries)
                    # Log number of entries converted.
                    LOGGER.debug('%d/%d entries found. %d converted.', tasks_ran, tasks_pending, entries_written)
                next_tasks.extend(self._thread_pool.submit(self._convert_subtree, hash)
                    for hash in next_hashes)
        except KeyboardInterrupt:
            # Cancel remaining futures to quickly shut down.
            LOGGER.info('Now Exiting...')
            while next_tasks:
                next_tasks.popleft().cancel()
    
    # Context manager methods.
    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        try:
            self._thread_pool.shutdown()
        finally:
            # Close the database, even if interrupted.
            self._merkle_db.close()
    