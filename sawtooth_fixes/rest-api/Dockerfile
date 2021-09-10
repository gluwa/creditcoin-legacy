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

FROM hyperledger/sawtooth-rest-api:1.0

RUN apt-get update
RUN apt-get -y install python3-pip python3 iputils-ping curl jq
RUN python3 -m pip install --upgrade protobuf

WORKDIR /

# Copy sawtooth rest api hotfix files to docker image
COPY hotfix/route_handlers.py      /usr/lib/python3/dist-packages/sawtooth_rest_api/route_handlers.py
COPY hotfix/client_block_pb2.py /usr/lib/python3/dist-packages/sawtooth_rest_api/protobuf/client_block_pb2.py
COPY hotfix/validator_pb2.py /usr/lib/python3/dist-packages/sawtooth_rest_api/protobuf/validator_pb2.py
COPY hotfix/processor_pb2.py /usr/lib/python3/dist-packages/sawtooth_rest_api/protobuf/processor_pb2.py