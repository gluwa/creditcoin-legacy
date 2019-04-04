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