# Copyright 2016 Intel Corporation
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

from asyncio.events import AbstractEventLoop
import logging
from threading import RLock
from concurrent.futures import Future


LOGGER = logging.getLogger(__name__)


class FutureResult(object):
    def __init__(self, message_type, content, connection_id=None):
        self.message_type = message_type
        self.content = content
        self.connection_id = connection_id


class FutureTimeoutError(Exception):
    pass


class FutureWrapper(object):
    def __init__(self, correlation_id, request=None, callback=None, loop=None):
        self._future = loop.create_future() if loop else Future() # type: Future
        self._event_loop = loop # type : AbstractEventLoop
        self.correlation_id = correlation_id
        self._request = request
        self._callback = callback

    def done(self):
        return self._future.done()

    def cancel(self):
        return self._future.cancel()

    @property
    def request(self):
        return self._request

    def result(self, timeout=None):
        return self._future.result(timeout=timeout)

    def set_result(self, result):
        if self._callback:
            l = lambda _: self._event_loop.run_in_executor(None, lambda: self._callback(self._request, result))
            self._future.add_done_callback(l)
        self._event_loop.call_soon_threadsafe(self._future.set_result, result)

class FutureCollectionKeyError(Exception):
    pass


class FutureCollection(object):
    def __init__(self, loop=None):
        self._futures = {}
        self._lock = RLock()
        self._event_loop = loop

    def put(self, future):
        with self._lock:
            self._futures[future.correlation_id] = future

    def set_result(self, correlation_id, result):
        with self._lock:
            future = self._get(correlation_id)
            future.set_result(result)
            self._remove(correlation_id)
    
    def _get(self, correlation_id):
        try:
            return self._futures[correlation_id]
        except KeyError:
            raise FutureCollectionKeyError(
                "no such correlation id: {}".format(correlation_id))

    def get(self, correlation_id):
        with self._lock:
            self._get(correlation_id)

    def _remove(self, correlation_id):
        if correlation_id in self._futures:
            f = self._futures[correlation_id]
            # if not f.done():
            #     f.cancel()
            del self._futures[correlation_id]
        else:
            raise FutureCollectionKeyError(
                "no such correlation id: {}".format(correlation_id))

    def remove(self, correlation_id):
        with self._lock:
            self._remove(correlation_id)
