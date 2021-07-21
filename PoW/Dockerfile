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

FROM hyperledger/sawtooth-validator:1.0

WORKDIR /

COPY . /home/Creditcoin/PoW

RUN apt-get update
RUN apt-get -y install python3-pip python3
RUN cd /home/Creditcoin/PoW/ && python3 setup.py sdist bdist_wheel
RUN cd /home/Creditcoin/PoW/dist && python3 -m pip install ./PoW-0.0.1.tar.gz
RUN python3 -m pip install --upgrade protobuf

# Copy sawtooth validator hotfix files to docker image
COPY hotfix/batch_injector.py      /usr/lib/python3/dist-packages/sawtooth_validator/journal/batch_injector.py
COPY hotfix/block_cache.py         /usr/lib/python3/dist-packages/sawtooth_validator/journal/block_cache.py
COPY hotfix/block_validator.py     /usr/lib/python3/dist-packages/sawtooth_validator/journal/block_validator.py
COPY hotfix/chain.py               /usr/lib/python3/dist-packages/sawtooth_validator/journal/chain.py
COPY hotfix/cli.py                 /usr/lib/python3/dist-packages/sawtooth_validator/server/cli.py
COPY hotfix/client_block_pb2.py    /usr/lib/python3/dist-packages/sawtooth_validator/protobuf/client_block_pb2.py
COPY hotfix/client_handlers.py     /usr/lib/python3/dist-packages/sawtooth_validator/state/client_handlers.py
COPY hotfix/completer.py           /usr/lib/python3/dist-packages/sawtooth_validator/journal/completer.py
COPY hotfix/component_handlers.py  /usr/lib/python3/dist-packages/sawtooth_validator/server/component_handlers.py
COPY hotfix/consensus.py           /usr/lib/python3/dist-packages/sawtooth_validator/journal/consensus/consensus.py
COPY hotfix/core.py                /usr/lib/python3/dist-packages/sawtooth_validator/server/core.py
COPY hotfix/executor.py            /usr/lib/python3/dist-packages/sawtooth_validator/execution/executor.py
COPY hotfix/genesis.py             /usr/lib/python3/dist-packages/sawtooth_validator/journal/genesis.py
COPY hotfix/gossip.py              /usr/lib/python3/dist-packages/sawtooth_validator/gossip/gossip.py
COPY hotfix/gossip_handlers.py     /usr/lib/python3/dist-packages/sawtooth_validator/gossip/gossip_handlers.py
COPY hotfix/handlers.py            /usr/lib/python3/dist-packages/sawtooth_validator/networking/handlers.py
COPY hotfix/interconnect.py        /usr/lib/python3/dist-packages/sawtooth_validator/networking/interconnect.py
COPY hotfix/merkle.py              /usr/lib/python3/dist-packages/sawtooth_validator/state/merkle.py
COPY hotfix/processor_pb2.py       /usr/lib/python3/dist-packages/sawtooth_validator/protobuf/processor_pb2.py
COPY hotfix/publisher.py           /usr/lib/python3/dist-packages/sawtooth_validator/journal/publisher.py
COPY hotfix/scheduler.py           /usr/lib/python3/dist-packages/sawtooth_validator/execution/scheduler.py
COPY hotfix/scheduler_parallel.py  /usr/lib/python3/dist-packages/sawtooth_validator/execution/scheduler_parallel.py
COPY hotfix/scheduler_serial.py	   /usr/lib/python3/dist-packages/sawtooth_validator/execution/scheduler_serial.py
COPY hotfix/timed_cache.py         /usr/lib/python3/dist-packages/sawtooth_validator/journal/timed_cache.py
COPY hotfix/validator_pb2.py       /usr/lib/python3/dist-packages/sawtooth_validator/protobuf/validator_pb2.py